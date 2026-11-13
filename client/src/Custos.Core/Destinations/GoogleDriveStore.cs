using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Custos.Core.Cloud;
using Custos.Core.Model;

namespace Custos.Core.Destinations;

/// <summary>
/// A folder in Google Drive (section 6.3), through the Drive API v3. CUSTOS sees only the files it made itself
/// (the right <c>drive.file</c>) and keeps them in the folder «CUSTOS» at the top of the Drive.
/// </summary>
/// <remarks>
/// Google Drive knows files by id, not by path, and lets a folder hold several files of the same name: paths are
/// looked up a folder at a time (the ids of folders are remembered), and a file written over another removes the
/// older ones of that name once it is complete. A file is uploaded in chunks of a resumable upload and appears when
/// its last chunk is there; deleting bypasses the bin.
/// </remarks>
public sealed class GoogleDriveStore : CloudStore
{
    internal const string ApiBase = "https://www.googleapis.com/drive/v3/";
    internal const string UploadBase = "https://www.googleapis.com/upload/drive/v3/files";
    internal const string FolderType = "application/vnd.google-apps.folder";
    internal const string TopFolder = "CUSTOS";

    private const string ItemFields = "id,name,mimeType,size,modifiedTime";

    private readonly Dictionary<string, string> _folderIds = new(StringComparer.Ordinal);

    public GoogleDriveStore(TaskDestination destination, CloudAccount account, SpeedLimiter? limiter = null)
        : base(destination, account, limiter)
    {
    }

    public override string Kind => DestinationKinds.GDrive;

    public override string AccountName()
    {
        using var document = SendJson(() => Get("about?fields=user(emailAddress,displayName)"), null);
        var user = document.RootElement.TryGetProperty("user", out var found) ? found : default;
        return Text(user, "emailAddress") ?? Text(user, "displayName") ?? ServiceName;
    }

    public override void CreateFolder(string path)
    {
        if (path.Length == 0)
        {
            RootId(create: true);
            return;
        }

        CreateFolderChain(path, FolderExists, one =>
        {
            string parent = FolderId(Parent(one) ?? string.Empty, create: true)!;
            var body = new JsonObject { ["name"] = Name(one), ["mimeType"] = FolderType, ["parents"] = new JsonArray(parent) };
            using var document = SendJson(() => new HttpRequestMessage(HttpMethod.Post, ApiBase + "files?fields=id") { Content = JsonBody(body) }, one);
            _folderIds[one] = Text(document.RootElement, "id") ?? throw new StoreException(Messages.Format(Messages.StoreFailed, ServiceName, one));
        });
    }

    public override bool FolderExists(string path) => FolderId(path, create: false) is not null;

    public override bool FileExists(string path) => Files(path).Count > 0;

    public override List<StoreEntry> List(string folder)
    {
        var result = new List<StoreEntry>();
        if (FolderId(folder, create: false) is not { } id)
        {
            return result;
        }

        foreach (var item in Children(id, name: null, pageSize: 1000))
        {
            result.Add(new StoreEntry(Combine(folder, item.Name), item.Name, item.IsFolder, item.IsFolder ? 0 : item.Size, item.ModifiedUtc));
            if (item.IsFolder)
            {
                _folderIds.TryAdd(Combine(folder, item.Name), item.Id);
            }
        }

        return result;
    }

    public override void DeleteFolder(string path)
    {
        if (FolderId(path, create: false) is { } id)
        {
            DeleteItem(id, path);
        }

        Forget(path);
    }

    public override bool RemoveEmptyFolder(string path)
    {
        if (FolderId(path, create: false) is not { } id)
        {
            return true;
        }

        if (Children(id, name: null, pageSize: 1, firstPageOnly: true).Count > 0)
        {
            return false;
        }

        DeleteItem(id, path);
        Forget(path);
        return true;
    }

    public override void MoveFolder(string source, string target)
    {
        string id = FolderId(source, create: false) ?? throw new FileNotFoundException(null, source);
        MoveItem(id, source, target);
        Forget(source);
        Forget(target);
    }

    public override Stream OpenRead(string path)
    {
        var file = Files(path).FirstOrDefault() ?? throw new FileNotFoundException(null, path);
        return new RangeReadStream(file.Size, offset => Download(file.Id, offset, path));
    }

    public override long GetFileSize(string path) => (Files(path).FirstOrDefault() ?? throw new FileNotFoundException(null, path)).Size;

    public override StoreWriter BeginWrite(string path, string tempSuffix, StoreWriteMode mode = StoreWriteMode.Blocks)
    {
        if (mode == StoreWriteMode.Seekable)
        {
            throw new NotSupportedException();
        }

        Cancellation.ThrowIfCancellationRequested();
        string folder = Parent(path) ?? string.Empty;
        CreateFolder(folder);
        return new Writer(this, path, FolderId(folder, create: true)!);
    }

    public override void DeleteFile(string path)
    {
        foreach (var file in Files(path))
        {
            DeleteItem(file.Id, path);
        }
    }

    public override void MoveFile(string source, string target)
    {
        var file = Files(source).FirstOrDefault() ?? throw new FileNotFoundException(null, source);
        DeleteFile(target);
        MoveItem(file.Id, source, target);
    }

    public override StoreSpace GetSpace(string path)
    {
        try
        {
            using var document = SendJson(() => Get("about?fields=storageQuota(limit,usage)"), path);
            var quota = document.RootElement.TryGetProperty("storageQuota", out var found) ? found : default;
            long limit = Number(quota, "limit");
            long usage = Number(quota, "usage");

            // No limit: an account whose storage is not counted.
            return limit > 0 && usage >= 0 ? new StoreSpace(limit, Math.Max(0, limit - usage)) : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    protected override Stream OpenAt(string path, long offset, long length)
    {
        var file = Files(path).FirstOrDefault() ?? throw new FileNotFoundException(null, path);
        return Download(file.Id, offset, path);
    }

    protected override bool IsTransient(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (base.IsTransient(response))
        {
            return true;
        }

        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            return false;
        }

        // Google answers 403 to too many requests too: those are waited out.
        response.Content.LoadIntoBufferAsync().GetAwaiter().GetResult();
        string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        return body.Contains("rateLimitExceeded", StringComparison.Ordinal) || body.Contains("userRateLimitExceeded", StringComparison.Ordinal);
    }

    protected override Exception Failure(HttpResponseMessage response, string? path)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            response.Content.LoadIntoBufferAsync().GetAwaiter().GetResult();
            string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (body.Contains("storageQuotaExceeded", StringComparison.Ordinal))
            {
                return new StoreException(Messages.Format(Messages.StoreFull, ServiceName));
            }
        }

        return base.Failure(response, path);
    }

    private static HttpRequestMessage Get(string relative) => new(HttpMethod.Get, ApiBase + relative);

    /// <summary>A name inside a query of the Drive API: in quotes, with \ and ' escaped.</summary>
    private static string Quoted(string text) => "'" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";

    /// <summary>The id of the folder «CUSTOS» at the top of the Drive; null if there is none and none is to be made.</summary>
    private string? RootId(bool create)
    {
        if (_folderIds.TryGetValue(string.Empty, out string? known))
        {
            return known;
        }

        var found = Children("root", TopFolder, pageSize: 10, firstPageOnly: true).FirstOrDefault(i => i.IsFolder);
        if (found is null)
        {
            if (!create)
            {
                return null;
            }

            var body = new JsonObject { ["name"] = TopFolder, ["mimeType"] = FolderType, ["parents"] = new JsonArray("root") };
            using var document = SendJson(() => new HttpRequestMessage(HttpMethod.Post, ApiBase + "files?fields=id") { Content = JsonBody(body) }, TopFolder);
            found = new Item(Text(document.RootElement, "id") ?? string.Empty, TopFolder, true, 0, DateTime.MinValue);
        }

        _folderIds[string.Empty] = found.Id;
        return found.Id;
    }

    /// <summary>The id of a folder by its path, looked up a folder at a time; null if it is not there.</summary>
    private string? FolderId(string path, bool create)
    {
        if (path.Length == 0)
        {
            return RootId(create);
        }

        if (_folderIds.TryGetValue(path, out string? known))
        {
            return known;
        }

        string? parent = FolderId(Parent(path) ?? string.Empty, create);
        if (parent is null)
        {
            return null;
        }

        var folder = Children(parent, Name(path), pageSize: 10, firstPageOnly: true).FirstOrDefault(i => i.IsFolder);
        if (folder is null)
        {
            if (!create)
            {
                return null;
            }

            CreateFolder(path);
            return _folderIds.TryGetValue(path, out string? made) ? made : null;
        }

        _folderIds[path] = folder.Id;
        return folder.Id;
    }

    /// <summary>The files of that name in its folder, the oldest first; empty if there are none.</summary>
    private List<Item> Files(string path)
    {
        if (path.Length == 0 || FolderId(Parent(path) ?? string.Empty, create: false) is not { } parent)
        {
            return [];
        }

        return [.. Children(parent, Name(path), pageSize: 100).Where(i => !i.IsFolder)];
    }

    /// <summary>What lies in a folder (not in the bin), or only what has the name.</summary>
    private List<Item> Children(string parentId, string? name, int pageSize, bool firstPageOnly = false)
    {
        string query = Quoted(parentId) + " in parents and trashed = false" + (name is null ? string.Empty : " and name = " + Quoted(name));
        var result = new List<Item>();
        string? page = null;
        do
        {
            Cancellation.ThrowIfCancellationRequested();
            string address = "files?spaces=drive&orderBy=createdTime&pageSize=" + pageSize.ToString(CultureInfo.InvariantCulture)
                + "&fields=" + Uri.EscapeDataString("nextPageToken,files(" + ItemFields + ")")
                + "&q=" + Uri.EscapeDataString(query)
                + (page is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(page));
            using var document = SendJson(() => Get(address), name);
            var root = document.RootElement;
            if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                foreach (var file in files.EnumerateArray())
                {
                    result.Add(new Item(
                        Text(file, "id") ?? string.Empty,
                        Text(file, "name") ?? string.Empty,
                        Text(file, "mimeType") == FolderType,
                        Math.Max(0, Number(file, "size")),
                        Time(file, "modifiedTime")));
                }
            }

            page = firstPageOnly ? null : Text(root, "nextPageToken");
        }
        while (page is not null);
        return result;
    }

    /// <summary>Deletes a file or a folder with everything in it, bypassing the bin; nothing happens if it is gone.</summary>
    private void DeleteItem(string id, string path)
    {
        using var response = Send(() => new HttpRequestMessage(HttpMethod.Delete, ApiBase + "files/" + Uri.EscapeDataString(id)), path);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            Check(response, path);
        }
    }

    /// <summary>Gives a file or folder another name and, if need be, another folder.</summary>
    private void MoveItem(string id, string source, string target)
    {
        string from = FolderId(Parent(source) ?? string.Empty, create: false) ?? throw new FileNotFoundException(null, source);
        string to = FolderId(Parent(target) ?? string.Empty, create: true)!;
        string address = ApiBase + "files/" + Uri.EscapeDataString(id) + "?fields=id"
            + (from == to ? string.Empty : "&addParents=" + Uri.EscapeDataString(to) + "&removeParents=" + Uri.EscapeDataString(from));
        var body = new JsonObject { ["name"] = Name(target) };
        SendChecked(() => new HttpRequestMessage(HttpMethod.Patch, address) { Content = JsonBody(body) }, source);
    }

    /// <summary>The folder and those below it are no longer where they were.</summary>
    private void Forget(string path)
    {
        ForgetFolders();
        foreach (string key in _folderIds.Keys.Where(k => k.Length > 0 && (k == path || k.StartsWith(path + "/", StringComparison.Ordinal))).ToList())
        {
            _folderIds.Remove(key);
        }
    }

    private GuardedStream Download(string id, long offset, string path)
    {
        var response = Send(
            () =>
            {
                var request = Get("files/" + Uri.EscapeDataString(id) + "?alt=media");
                if (offset > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(offset, null);
                }

                return request;
            },
            path);
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

    private sealed record Item(string Id, string Name, bool IsFolder, long Size, DateTime ModifiedUtc);

    /// <summary>
    /// A file being uploaded: a small one with one request, a larger one with a resumable upload whose chunks are
    /// sent again from where Google says it stopped. Once complete, older files of the same name are removed.
    /// </summary>
    private sealed class Writer(GoogleDriveStore store, string path, string parentId) : ChunkedWriter(store, path)
    {
        private readonly GoogleDriveStore _store = store;
        private string? _session;
        private string? _fileId;

        protected override void SendChunk(ArraySegment<byte> data, bool last, long total)
        {
            if (_session is null && last)
            {
                _fileId = UploadWhole(data);
            }
            else
            {
                _session ??= StartSession();
                Started = true;
                SendPart(data, last, total);
            }

            if (last)
            {
                RemoveOlder();
            }
        }

        protected override void GiveUp()
        {
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var request = new HttpRequestMessage(HttpMethod.Delete, _session);
                using var response = _store.Transfer.Send(request, limit.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
            {
                // The upload expires on its own after a week.
            }
        }

        protected override void Remove()
        {
            if (_fileId is not null)
            {
                _store.DeleteItem(_fileId, Path);
            }
        }

        private JsonObject Metadata() => new() { ["name"] = _store.Name(Path), ["parents"] = new JsonArray(parentId) };

        /// <summary>A file that fits into one chunk: metadata and data in one request.</summary>
        private string UploadWhole(ArraySegment<byte> data)
        {
            using var document = _store.SendJson(
                () =>
                {
                    var content = new MultipartContent("related");
                    content.Add(JsonBody(Metadata()));
                    content.Add(_store.Body(data));
                    return new HttpRequestMessage(HttpMethod.Post, UploadBase + "?uploadType=multipart&fields=id") { Content = content };
                },
                Path,
                _store.TransferTime(data.Count));
            Sent = data.Count;
            return Text(document.RootElement, "id") ?? throw new StoreException(Messages.Format(Messages.StoreUploadRefused));
        }

        private string StartSession()
        {
            using var response = _store.Send(
                () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, UploadBase + "?uploadType=resumable&fields=id") { Content = JsonBody(Metadata()) };
                    request.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
                    return request;
                },
                Path);
            _store.Check(response, Path);
            return response.Headers.Location?.AbsoluteUri ?? throw new StoreException(Messages.Format(Messages.StoreFailed, _store.ServiceName, "Location"));
        }

        /// <summary>Sends a chunk; after a failure asks how much arrived and sends the rest of it.</summary>
        private void SendPart(ArraySegment<byte> data, bool last, long total)
        {
            int offset = 0;
            int failures = 0;
            while (true)
            {
                var piece = data[offset..];
                long from = Sent + offset;
                bool broke = false;
                Status status;
                try
                {
                    using var response = _store.Send(
                        () =>
                        {
                            var request = new HttpRequestMessage(HttpMethod.Put, _session) { Content = _store.Body(piece) };
                            request.Content.Headers.ContentRange = piece.Count == 0
                                ? new ContentRangeHeaderValue(total)
                                : last ? new ContentRangeHeaderValue(from, from + piece.Count - 1, total) : new ContentRangeHeaderValue(from, from + piece.Count - 1);
                            return request;
                        },
                        Path,
                        _store.TransferTime(piece.Count),
                        retry: false);
                    status = Read(response);
                    if (status.Transient)
                    {
                        throw new StoreException(Messages.Format(Messages.StoreHttpFailed, _store.ServiceName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), Path));
                    }
                }
                catch (StoreException ex) when (ex is not CloudAccessRevokedException && ++failures < UploadAttempts)
                {
                    broke = true;
                    _store.Wait(failures, null);
                    status = Query(last ? total : null);
                }

                if (status.Done)
                {
                    _fileId = status.Id;
                    Sent = last ? total : Sent + data.Count;
                    return;
                }

                long arrived = status.Received - Sent;
                if (arrived < 0 || arrived > data.Count)
                {
                    throw new StoreException(Messages.Format(Messages.CloudUploadLost, _store.ServiceName, Path));
                }

                if (arrived == data.Count && !last)
                {
                    Sent += data.Count;
                    return;
                }

                if (!broke && arrived <= offset && ++failures >= UploadAttempts)
                {
                    // Google keeps taking nothing of what is sent: not a state to go on from.
                    throw new StoreException(Messages.Format(Messages.CloudUploadLost, _store.ServiceName, Path));
                }

                offset = (int)arrived;
            }
        }

        /// <summary>Asks how much of the file has arrived.</summary>
        private Status Query(long? total)
        {
            using var response = _store.Send(
                () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Put, _session) { Content = new ByteArrayContent([]) };
                    request.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes */" + (total?.ToString(CultureInfo.InvariantCulture) ?? "*"));
                    return request;
                },
                Path);
            var status = Read(response);
            return status.Transient ? throw new StoreException(Messages.Format(Messages.StoreFailed, _store.ServiceName, Messages.StoreNoAnswer)) : status;
        }

        /// <summary>What an answer to a chunk or a question says.</summary>
        private Status Read(HttpResponseMessage response)
        {
            int code = (int)response.StatusCode;
            if (code is 200 or 201)
            {
                using var document = ReadJson(response);
                return new Status(true, -1, Text(document.RootElement, "id"), false);
            }

            if (code == 308)
            {
                // "bytes=0-1048575": what arrived; no header: nothing yet.
                long received = 0;
                if (response.Headers.TryGetValues("Range", out var values) && values.FirstOrDefault() is { } range)
                {
                    int dash = range.LastIndexOf('-');
                    if (dash > 0 && long.TryParse(range[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out long last))
                    {
                        received = last + 1;
                    }
                }

                return new Status(false, received, null, false);
            }

            if (code is 404 or 410)
            {
                throw new StoreException(Messages.Format(Messages.CloudUploadExpired, _store.ServiceName, Path));
            }

            if (code is 429 || code >= 500)
            {
                return new Status(false, -1, null, true);
            }

            throw _store.Failure(response, Path);
        }

        /// <summary>Older files of the same name: the new one takes their place.</summary>
        private void RemoveOlder()
        {
            foreach (var older in _store.Children(parentId, _store.Name(Path), pageSize: 100).Where(i => !i.IsFolder && i.Id != _fileId))
            {
                _store.DeleteItem(older.Id, Path);
            }
        }

        private sealed record Status(bool Done, long Received, string? Id, bool Transient);
    }
}
