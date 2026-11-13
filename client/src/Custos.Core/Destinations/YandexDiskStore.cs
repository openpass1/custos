using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Custos.Core.Cloud;
using Custos.Core.Model;

namespace Custos.Core.Destinations;

/// <summary>
/// A folder in the app folder of a Yandex Disk (section 6.3), through the REST API of the Disk. CUSTOS sees only
/// its app folder («Приложения/CUSTOS», the path <c>app:/</c>).
/// </summary>
/// <remarks>
/// A file is sent with one PUT to the address the Disk hands out for it, its length not known in advance. The Disk
/// has no way to go on with an upload that broke off: that file fails, and the archive in parts («Размер части»)
/// keeps such a loss small. Without a Yandex 360 subscription the Disk takes files of up to 1 GB. The file appears
/// when the upload is complete. Deleting bypasses the bin; long operations are waited for.
/// </remarks>
public sealed class YandexDiskStore : CloudStore
{
    internal const string ApiBase = "https://cloud-api.yandex.net/v1/disk";

    /// <summary>How long the Disk may take to put a received file in its place or to finish an operation.</summary>
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(30);

    private bool _appFolderChecked;

    public YandexDiskStore(TaskDestination destination, CloudAccount account, SpeedLimiter? limiter = null)
        : base(destination, account, limiter)
    {
    }

    public override string Kind => DestinationKinds.YaDisk;

    public override string AccountName()
    {
        using var document = SendJson(() => new HttpRequestMessage(HttpMethod.Get, ApiBase + "/?fields=user"), null);
        var user = document.RootElement.TryGetProperty("user", out var found) ? found : default;
        return Text(user, "login") is { Length: > 0 } login ? login : Text(user, "display_name") ?? ServiceName;
    }

    public override void CreateFolder(string path)
    {
        EnsureAppFolder();
        CreateFolderChain(path, FolderExists, one =>
        {
            using var response = Send(() => new HttpRequestMessage(HttpMethod.Put, Address("/resources", one)), one);

            // 409: it is there already.
            if (response.StatusCode != HttpStatusCode.Conflict)
            {
                Check(response, one);
            }
        });
    }

    public override bool FolderExists(string path)
    {
        if (path.Length == 0 || IsKnownFolder(path))
        {
            return true;
        }

        bool exists;
        using (var found = Resource(path, "type", 0))
        {
            exists = found is not null && Text(found.RootElement, "type") == "dir";
        }

        if (exists)
        {
            RememberFolder(path);
        }

        return exists;
    }

    public override bool FileExists(string path)
    {
        using var found = Resource(path, "type", 0);
        return found is not null && Text(found.RootElement, "type") == "file";
    }

    public override List<StoreEntry> List(string folder)
    {
        var result = new List<StoreEntry>();
        const int Page = 1000;
        for (int offset = 0; ; offset += Page)
        {
            Cancellation.ThrowIfCancellationRequested();
            using var page = Resource(folder, "_embedded.items.name,_embedded.items.type,_embedded.items.size,_embedded.items.modified,_embedded.total", Page, offset);
            if (page is null || !page.RootElement.TryGetProperty("_embedded", out var embedded))
            {
                return result;
            }

            int count = 0;
            if (embedded.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    count++;
                    string name = Text(item, "name") ?? string.Empty;
                    if (name.Length == 0)
                    {
                        continue;
                    }

                    bool isFolder = Text(item, "type") == "dir";
                    result.Add(new StoreEntry(Combine(folder, name), name, isFolder, isFolder ? 0 : Math.Max(0, Number(item, "size")), Time(item, "modified")));
                }
            }

            if (count < Page || offset + Page >= Number(embedded, "total"))
            {
                return result;
            }
        }
    }

    public override void DeleteFolder(string path)
    {
        ForgetFolders();
        Delete(path);
    }

    public override bool RemoveEmptyFolder(string path)
    {
        ForgetFolders();
        using (var found = Resource(path, "_embedded.total", 1))
        {
            if (found is null)
            {
                return true;
            }

            if (found.RootElement.TryGetProperty("_embedded", out var embedded) && Number(embedded, "total") > 0)
            {
                return false;
            }
        }

        Delete(path);
        return true;
    }

    public override void MoveFolder(string source, string target)
    {
        ForgetFolders();
        Move(source, target, overwrite: false);
    }

    public override long GetFileSize(string path)
    {
        using var found = Resource(path, "type,size", 0);
        return found is not null && Text(found.RootElement, "type") == "file" ? Math.Max(0, Number(found.RootElement, "size")) : throw new FileNotFoundException(null, path);
    }

    public override StoreWriter BeginWrite(string path, string tempSuffix, StoreWriteMode mode = StoreWriteMode.Blocks)
    {
        if (mode == StoreWriteMode.Seekable)
        {
            throw new NotSupportedException();
        }

        Cancellation.ThrowIfCancellationRequested();
        return new Writer(this, path);
    }

    public override void DeleteFile(string path) => Delete(path);

    public override void MoveFile(string source, string target) => Move(source, target, overwrite: true);

    public override StoreSpace GetSpace(string path)
    {
        try
        {
            using var document = SendJson(() => new HttpRequestMessage(HttpMethod.Get, ApiBase + "/?fields=total_space,used_space"), path);
            long total = Number(document.RootElement, "total_space");
            long used = Number(document.RootElement, "used_space");
            return total > 0 && used >= 0 ? new StoreSpace(total, Math.Max(0, total - used)) : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    protected override AuthenticationHeaderValue Authorization(string token) => new("OAuth", token);

    /// <summary>423: the resource is busy with another operation for a moment.</summary>
    protected override bool IsTransient(HttpResponseMessage response) =>
        base.IsTransient(response) || response?.StatusCode == HttpStatusCode.Locked;

    protected override Stream OpenAt(string path, long offset, long length)
    {
        string href = Link("/resources/download", path, overwrite: null);
        var response = Send(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, href);
                if (offset > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(offset, null);
                }

                return request;
            },
            path,
            client: Transfer);
        try
        {
            Check(response, path);
            var body = response.Content.ReadAsStream(Cancellation);
            if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                throw new StoreException(Messages.Format(Messages.StoreFailed, ServiceName, "Range"));
            }

            return new GuardedStream(body, ex => Translate(ex, path), response.Dispose);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>The address of a request about a path in the app folder.</summary>
    private static string Address(string endpoint, string path, string? more = null) =>
        ApiBase + endpoint + "?path=" + Uri.EscapeDataString(ApiPath(path)) + (more is null ? string.Empty : "&" + more);

    private static string ApiPath(string path) => "app:/" + path;

    /// <summary>The app folder is made when it is first needed.</summary>
    private void EnsureAppFolder()
    {
        if (_appFolderChecked)
        {
            return;
        }

        using (var found = Resource(string.Empty, "type", 0))
        {
            if (found is null)
            {
                using var response = Send(() => new HttpRequestMessage(HttpMethod.Put, Address("/resources", string.Empty)), string.Empty);
                if (response.StatusCode != HttpStatusCode.Conflict)
                {
                    Check(response, string.Empty);
                }
            }
        }

        _appFolderChecked = true;
    }

    /// <summary>The facts about a file or folder; null if nothing is there.</summary>
    private JsonDocument? Resource(string path, string fields, int limit, int offset = 0)
    {
        string more = "fields=" + Uri.EscapeDataString(fields) + "&limit=" + limit.ToString(CultureInfo.InvariantCulture)
            + (offset > 0 ? "&offset=" + offset.ToString(CultureInfo.InvariantCulture) : string.Empty);
        using var response = Send(() => new HttpRequestMessage(HttpMethod.Get, Address("/resources", path, more)), path);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        Check(response, path);
        return ReadJson(response);
    }

    /// <summary>An address the Disk hands out for one file (to upload or download it).</summary>
    private string Link(string endpoint, string path, bool? overwrite)
    {
        using var document = SendJson(() => new HttpRequestMessage(HttpMethod.Get, Address(endpoint, path, overwrite is null ? null : "overwrite=" + (overwrite.Value ? "true" : "false"))), path);
        return Text(document.RootElement, "href") ?? throw new StoreException(Messages.Format(Messages.StoreFailed, ServiceName, endpoint));
    }

    private void Delete(string path)
    {
        using var response = Send(() => new HttpRequestMessage(HttpMethod.Delete, Address("/resources", path, "permanently=true")), path);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        Check(response, path);
        Await(response, path);
    }

    private void Move(string source, string target, bool overwrite)
    {
        string address = ApiBase + "/resources/move?from=" + Uri.EscapeDataString(ApiPath(source)) + "&path=" + Uri.EscapeDataString(ApiPath(target))
            + "&overwrite=" + (overwrite ? "true" : "false");
        using var response = Send(() => new HttpRequestMessage(HttpMethod.Post, address), source);
        if (response.StatusCode == HttpStatusCode.NotFound && Resource(target, "type", 0) is { } moved)
        {
            // An earlier attempt moved it; only its answer was lost.
            moved.Dispose();
            return;
        }

        Check(response, source);
        Await(response, source);
    }

    /// <summary>202: the Disk does it in the background; its operation is followed until it is done.</summary>
    private void Await(HttpResponseMessage response, string path)
    {
        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            return;
        }

        string? href;
        using (var document = ReadJson(response))
        {
            href = Text(document.RootElement, "href");
        }

        if (href is null)
        {
            return;
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int attempt = 1; clock.Elapsed < OperationTimeout; attempt++)
        {
            using var status = SendJson(() => new HttpRequestMessage(HttpMethod.Get, href), path);
            switch (Text(status.RootElement, "status"))
            {
                case "success":
                    return;
                case "failed":
                    throw new StoreException(Messages.Format(Messages.StoreFailed, ServiceName, path));
            }

            Wait(Math.Min(attempt, 4), null);
        }

        throw new StoreException(Messages.Format(Messages.StoreFailed, ServiceName, Messages.StoreNoAnswer));
    }

    /// <summary>Waits until a file the Disk took (202) is in its place.</summary>
    private void AwaitFile(string path, long length)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int attempt = 1; clock.Elapsed < OperationTimeout; attempt++)
        {
            using (var found = Resource(path, "type,size", 0))
            {
                if (found is not null && Text(found.RootElement, "type") == "file" && Number(found.RootElement, "size") == length)
                {
                    return;
                }
            }

            Wait(Math.Min(attempt, 4), null);
        }

        throw new StoreException(Messages.Format(Messages.StoreFailed, ServiceName, Messages.StoreNoAnswer));
    }

    /// <summary>
    /// A file sent with one PUT whose body the engine writes through a pipe; the request runs on a thread of its own.
    /// It has its name once the Disk has it.
    /// </summary>
    private sealed class Writer : StoreWriter
    {
        private readonly YandexDiskStore _store;
        private readonly string _path;
        private readonly BlockingPipe _pipe = new();
        private readonly Task<HttpStatusCode> _upload;
        private bool _closed;
        private bool _stored;

        public Writer(YandexDiskStore store, string path)
        {
            _store = store;
            _path = path;

            // The address first: a refusal (no room, no rights) comes before any data.
            string href = store.Link("/resources/upload", path, overwrite: true);
            Stream = store.Limited(_pipe.Writer);
            _upload = Task.Factory.StartNew(
                () =>
                {
                    try
                    {
                        return Put(href);
                    }
                    catch (Exception ex)
                    {
                        _pipe.FailReader(store.Translate(ex, path) ?? ex);
                        throw;
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        public override Stream Stream { get; }

        public override string ReadPath => _path;

        public override bool VisibleWhenClosed => true;

        public override void Close()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _pipe.Complete();
            HttpStatusCode status;
            try
            {
                status = _upload.GetAwaiter().GetResult();
            }
            catch (Exception ex) when (_store.Translate(ex, _path) is { } failure && !ReferenceEquals(failure, ex))
            {
                throw failure;
            }

            if (status == HttpStatusCode.Accepted)
            {
                _store.AwaitFile(_path, _pipe.Written);
            }

            _stored = true;
        }

        protected override void CommitCore()
        {
            // The file got its name when the Disk took it.
        }

        protected override void AbortCore()
        {
            if (!_closed)
            {
                _closed = true;
                _pipe.Abort();
                try
                {
                    _upload.GetAwaiter().GetResult();
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or StoreException or UnauthorizedAccessException)
                {
                    // Broken off on purpose.
                }
            }

            if (_stored)
            {
                _store.Delete(_path);
            }
        }

        private HttpStatusCode Put(string href)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, href) { Content = new StreamContent(_pipe.Reader, 256 * 1024) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(_store.Cancellation);
            using var response = _store.Transfer.Send(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            _store.Check(response, _path);
            return response.StatusCode;
        }
    }
}
