using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Custos.Core.Cloud;
using Custos.Core.Model;

namespace Custos.Core.Destinations;

/// <summary>
/// A folder in a cloud drive of a personal account (section 6.3, «Облачные диски»): Google Drive, Yandex Disk or
/// Dropbox, through their REST interfaces. Paths are relative to the folder of CUSTOS in the account, with "/"
/// between their parts; the root of that folder is the empty path.
/// </summary>
/// <remarks>
/// Every request carries the access token of the account; a refused token is renewed once. A request the service
/// could not take now (too many requests, a server error, a broken connection) is tried again after a pause that
/// doubles each time. A file is uploaded in chunks kept in memory, so a chunk that broke off is sent again; it
/// appears in the drive only when its last chunk is there.
/// </remarks>
public abstract class CloudStore : RemoteStore
{
    /// <summary>What is kept in memory and sent in one request of an upload: a multiple of 256 KB (Google).</summary>
    internal const int ChunkSize = 16 * 1024 * 1024;

    private const int Attempts = 6;

    /// <summary>The slowest upload that is still an upload and not a dead connection, in bytes per second.</summary>
    private const long SlowestUpload = 32 * 1024;

    private readonly HttpClient _transfer;

    protected CloudStore(TaskDestination destination, CloudAccount account, SpeedLimiter? limiter)
    {
        ArgumentNullException.ThrowIfNull(destination);
        CloudAccount = account ?? throw new ArgumentNullException(nameof(account));
        Limiter = limiter;
        Root = FolderOf(destination.Path);
        RetryUnit = account.RetryUnit;
        if (account.Handler is { } handler)
        {
            Http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            _transfer = Http;
        }
        else
        {
            Http = new HttpClient(Handler(redirects: false)) { Timeout = Timeout.InfiniteTimeSpan };

            // Files are fetched from addresses the service hands out, which may send the request on elsewhere. These
            // requests never carry the token.
            _transfer = new HttpClient(Handler(redirects: true)) { Timeout = Timeout.InfiniteTimeSpan };
        }

        Http.DefaultRequestHeaders.UserAgent.ParseAdd("CUSTOS/" + CustosInfo.Version);
    }

    public override string Root { get; }

    public override string Label => LabelOf(Kind, Root);

    public override string? Account => CloudAccount.User;

    public override string CacheKey => Kind + ":" + CloudAccount.User;

    /// <summary>«Google Drive», «Яндекс Диск», «Dropbox».</summary>
    public string ServiceName => CloudService.NameOf(Kind);

    protected CloudAccount CloudAccount { get; }

    /// <summary>The connection to the service: requests with the token.</summary>
    protected HttpClient Http { get; }

    /// <summary>Requests to addresses the service handed out for one file (Yandex Disk), without the token.</summary>
    protected HttpClient Transfer => _transfer;

    protected TimeSpan RetryUnit { get; }

    /// <summary>The folder of a destination: the path as typed, without "/" at either end ("" = the folder of CUSTOS).</summary>
    public static string FolderOf(string? path)
    {
        string clean = CleanRoot(path).Trim('/');
        return clean == "." ? string.Empty : clean;
    }

    /// <summary>A destination for people: «Google Drive: CUSTOS/Backups».</summary>
    public static string LabelOf(string kind, string? path)
    {
        string folder = FolderOf(path);
        return CloudService.NameOf(kind) + ": " + CloudService.AppFolderOf(kind) + (folder.Length > 0 ? "/" + folder : string.Empty);
    }

    /// <summary>The name of the account as the service tells it (its address or login), for the stored account.</summary>
    public abstract string AccountName();

    public override Stream OpenRead(string path)
    {
        long length = GetFileSize(path);
        return new RangeReadStream(length, offset => OpenAt(path, offset, length));
    }

    /// <summary>Starts a download of a file at a position.</summary>
    protected abstract Stream OpenAt(string path, long offset, long length);

    protected internal override Exception? Translate(Exception exception, string? path) => exception switch
    {
        StoreException or FileNotFoundException or UnauthorizedAccessException => exception,
        HttpRequestException or AuthenticationException or TimeoutException or IOException =>
            new StoreException(Messages.Format(Messages.StoreUnreachable, ServiceName, Deepest(exception).Message), exception),
        OperationCanceledException when !Cancellation.IsCancellationRequested =>
            new StoreException(Messages.Format(Messages.StoreFailed, ServiceName, Messages.StoreNoAnswer), exception),
        JsonException => new StoreException(Messages.Format(Messages.StoreFailed, ServiceName, exception.Message), exception),
        _ => null,
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (!ReferenceEquals(_transfer, Http))
            {
                _transfer.Dispose();
            }

            Http.Dispose();
        }

        base.Dispose(disposing);
    }

    // ---- Requests ----------------------------------------------------------------------------------------------

    /// <summary>The header that carries the token: «Bearer …», at Yandex «OAuth …».</summary>
    protected virtual AuthenticationHeaderValue Authorization(string token) => new("Bearer", token);

    /// <summary>
    /// Sends a request and returns the answer, whatever it is, once the service took it: a refused token is renewed
    /// once, and a request the service could not take now is tried again. <paramref name="make"/> builds the request
    /// anew for every attempt.
    /// </summary>
    /// <param name="timeout">How long the whole request may take; null = <see cref="RemoteStore.RequestTimeout"/>.</param>
    /// <param name="client">The client to send with; null = <see cref="Http"/> with the token.</param>
    /// <param name="retry">The request may be sent again (it changes nothing twice, or its failure is handled).</param>
    protected HttpResponseMessage Send(Func<HttpRequestMessage> make, string? path, TimeSpan? timeout = null, HttpClient? client = null, bool retry = true)
    {
        bool renewed = false;
        bool authorize = client is null;
        for (int attempt = 1; ; attempt++)
        {
            Cancellation.ThrowIfCancellationRequested();
            using var request = make();
            if (authorize)
            {
                request.Headers.Authorization = Authorization(Token(renewed && attempt == 1));
            }

            HttpResponseMessage response;
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(Cancellation))
            {
                limit.CancelAfter(timeout ?? RequestTimeout);
                try
                {
                    response = (client ?? Http).Send(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
                }
                catch (Exception ex) when (retry && attempt < Attempts && Translate(ex, path) is StoreException)
                {
                    Wait(attempt, null);
                    continue;
                }
                catch (Exception ex) when (Translate(ex, path) is { } failure && !ReferenceEquals(failure, ex))
                {
                    throw failure;
                }
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized && authorize && !renewed)
            {
                // The access token ran out early or was revoked: a new one is asked for once.
                response.Dispose();
                renewed = true;
                attempt = 0;
                continue;
            }

            if (retry && attempt < Attempts && IsTransient(response))
            {
                var after = response.Headers.RetryAfter?.Delta;
                response.Dispose();
                Wait(attempt, after);
                continue;
            }

            return response;
        }
    }

    /// <summary>Sends a request and fails unless the service says it is done.</summary>
    protected void SendChecked(Func<HttpRequestMessage> make, string? path, TimeSpan? timeout = null)
    {
        using var response = Send(make, path, timeout);
        Check(response, path);
    }

    /// <summary>Sends a request and reads its answer as JSON; fails unless the service says it is done.</summary>
    protected JsonDocument SendJson(Func<HttpRequestMessage> make, string? path, TimeSpan? timeout = null)
    {
        using var response = Send(make, path, timeout);
        Check(response, path);
        return ReadJson(response);
    }

    /// <summary>The answer is worth waiting for: too many requests, or a server that will be back.</summary>
    protected virtual bool IsTransient(HttpResponseMessage response) =>
        (int)response.StatusCode is 429 or 500 or 502 or 503 or 504;

    /// <summary>A refusal of the service as the engine knows it; the text of the service is added where it has one.</summary>
    protected void Check(HttpResponseMessage response, string? path)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!response.IsSuccessStatusCode)
        {
            throw Failure(response, path);
        }
    }

    /// <summary>The failure for an answer that is not a success.</summary>
    protected virtual Exception Failure(HttpResponseMessage response, string? path)
    {
        string detail = ErrorText(response);
        int code = (int)response.StatusCode;
        string text = code.ToString(CultureInfo.InvariantCulture) + " " + response.ReasonPhrase + (detail.Length > 0 ? ": " + detail : string.Empty);
        string message = Messages.Format(Messages.StoreHttpFailed, ServiceName, text, path ?? Root);
        return response.StatusCode switch
        {
            HttpStatusCode.NotFound => new FileNotFoundException(message, path),
            HttpStatusCode.Unauthorized => new CloudAccessRevokedException(Messages.Format(Messages.CloudAccessRevoked, CloudAccount.User, ServiceName)),
            HttpStatusCode.Forbidden => new UnauthorizedAccessException(message),
            HttpStatusCode.InsufficientStorage => new StoreException(Messages.Format(Messages.StoreFull, ServiceName)),
            HttpStatusCode.RequestEntityTooLarge => new StoreException(Messages.Format(Messages.CloudFileTooLarge, ServiceName, path ?? Root)),
            _ => new StoreException(message),
        };
    }

    /// <summary>What the service wrote about a failure, short.</summary>
    protected static string ErrorText(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        string body;
        try
        {
            using var reader = new StreamReader(response.Content.ReadAsStream(), Encoding.UTF8);
            char[] buffer = new char[4096];
            int read = reader.ReadBlock(buffer, 0, buffer.Length);
            body = new string(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ObjectDisposedException or InvalidOperationException)
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                // Google: {"error": {"message": …}}; Yandex: {"message": …, "description": …}; Dropbox: {"error_summary": …}.
                foreach (string name in (string[])["error_summary", "message", "description", "error_description"])
                {
                    if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
                    {
                        return text;
                    }
                }

                if (root.TryGetProperty("error", out var error))
                {
                    if (error.ValueKind == JsonValueKind.String)
                    {
                        return error.GetString() ?? string.Empty;
                    }

                    if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                    {
                        return message.GetString() ?? string.Empty;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON: the text itself, short.
        }

        body = body.Trim();
        return body.Length <= 200 ? body : body[..200] + "…";
    }

    protected static JsonDocument ReadJson(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        using var stream = response.Content.ReadAsStream();
        return JsonDocument.Parse(stream);
    }

    protected static StringContent JsonBody(JsonNode body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
    }

    protected static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    protected static long Number(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return -1;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out long number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) => parsed,
            _ => -1,
        };
    }

    protected static DateTime Time(JsonElement element, string name) =>
        Text(element, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? time.UtcDateTime
            : DateTime.MinValue;

    /// <summary>What is sent of a chunk: from memory, at the speed the destination allows.</summary>
    protected HttpContent Body(ArraySegment<byte> data)
    {
        HttpContent content = Limiter is null
            ? new ByteArrayContent(data.Array!, data.Offset, data.Count)
            : new StreamContent(new ThrottledStream(new MemoryStream(data.Array!, data.Offset, data.Count, writable: false), Limiter, Cancellation));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = data.Count;
        return content;
    }

    /// <summary>How long sending so many bytes may take.</summary>
    protected TimeSpan TransferTime(long bytes)
    {
        long rate = Limiter is null ? SlowestUpload : Math.Clamp(Limiter.BytesPerSecond / 2, 1024, SlowestUpload);
        return RequestTimeout + TimeSpan.FromSeconds(bytes / (double)rate);
    }

    /// <summary>Waits before the next attempt: as long as the service asks, or 1, 2, 4 … units, at most a minute.</summary>
    protected void Wait(int attempt, TimeSpan? asked)
    {
        var pause = asked is { } wanted && wanted > TimeSpan.Zero
            ? (wanted < TimeSpan.FromMinutes(1) ? wanted : TimeSpan.FromMinutes(1))
            : RetryUnit * Math.Pow(2, Math.Min(attempt - 1, 6));
        if (pause > TimeSpan.FromMinutes(1))
        {
            pause = TimeSpan.FromMinutes(1);
        }

        Cancellation.WaitHandle.WaitOne(pause);
        Cancellation.ThrowIfCancellationRequested();
    }

    /// <summary>How many times a chunk of an upload is tried.</summary>
    protected const int UploadAttempts = Attempts;

    private string Token(bool renew) => CloudAccount.Tokens.AccessToken(renew, Cancellation);

    private static Exception Deepest(Exception exception)
    {
        while (exception.InnerException is { } inner)
        {
            exception = inner;
        }

        return exception;
    }

    private static SocketsHttpHandler Handler(bool redirects) => new()
    {
        AllowAutoRedirect = redirects,
        ConnectTimeout = ConnectTimeout,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    // ---- Uploads -----------------------------------------------------------------------------------------------

    /// <summary>
    /// A file uploaded in chunks kept in memory: every full chunk is sent while the engine goes on writing into the
    /// next one, and the last one finishes the file. A chunk that broke off is sent again from where the service
    /// says it stopped. The file has its name when the last chunk is there.
    /// </summary>
    protected abstract class ChunkedWriter : StoreWriter
    {
        private readonly ChunkStream _stream;
        private bool _closed;
        private bool _finished;

        protected ChunkedWriter(CloudStore store, string path)
        {
            Store = store ?? throw new ArgumentNullException(nameof(store));
            Path = path;
            _stream = new ChunkStream(this);
        }

        protected CloudStore Store { get; }

        /// <summary>The file in the drive.</summary>
        protected string Path { get; }

        /// <summary>Bytes the service has taken so far.</summary>
        protected long Sent { get; set; }

        /// <summary>Something was sent already: there is an upload to give up.</summary>
        protected bool Started { get; set; }

        public override Stream Stream => _stream;

        public override string ReadPath => Path;

        public override bool VisibleWhenClosed => true;

        public override void Close()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            var data = _stream.Take();
            try
            {
                SendChunk(data, last: true, total: _stream.Length);
                _finished = true;
            }
            finally
            {
                _stream.Release();
            }
        }

        protected override void CommitCore()
        {
            // The file got its name with its last chunk.
        }

        protected override void AbortCore()
        {
            _stream.Release();
            if (_finished)
            {
                Remove();
            }
            else if (Started)
            {
                GiveUp();
            }
        }

        /// <summary>Sends the chunk that starts at <see cref="Sent"/> and moves <see cref="Sent"/> past it.</summary>
        /// <param name="total">The size of the whole file with the last chunk; -1 before.</param>
        protected abstract void SendChunk(ArraySegment<byte> data, bool last, long total);

        /// <summary>Gives up an upload that is not finished. Failures are ignored: it expires on its own.</summary>
        protected abstract void GiveUp();

        /// <summary>Deletes the finished file.</summary>
        protected abstract void Remove();

        /// <summary>Collects what the engine writes and hands over every full chunk.</summary>
        private sealed class ChunkStream(ChunkedWriter writer) : Stream
        {
            private byte[] _buffer = [];
            private int _count;
            private long _written;

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => true;

            public override long Length => _written;

            public override long Position
            {
                get => _written;
                set => throw new NotSupportedException();
            }

            public ArraySegment<byte> Take() => new(_buffer, 0, _count);

            public void Release()
            {
                _buffer = [];
                _count = 0;
            }

            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                while (buffer.Length > 0)
                {
                    if (_count >= ChunkSize)
                    {
                        writer.SendChunk(new ArraySegment<byte>(_buffer, 0, _count), last: false, total: -1);
                        _count = 0;
                    }

                    int copied = Math.Min(buffer.Length, ChunkSize - _count);
                    if (_buffer.Length < _count + copied)
                    {
                        // Small files stay small: the buffer grows as the file does, up to a chunk.
                        Array.Resize(ref _buffer, (int)Math.Min(ChunkSize, Math.Max((long)_count + copied, Math.Max(64 * 1024, _buffer.Length * 2L))));
                    }

                    buffer[..copied].CopyTo(_buffer.AsSpan(_count));
                    _count += copied;
                    _written += copied;
                    buffer = buffer[copied..];
                }
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
