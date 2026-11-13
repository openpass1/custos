using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Custos.Core.Cloud;
using Custos.Core.Model;

namespace Custos.Core.Destinations;

/// <summary>
/// A folder in the app folder of a Dropbox account (section 6.3), through the API v2. CUSTOS sees only its app
/// folder («Приложения/CUSTOS»). A file is uploaded in chunks of an upload session and appears when the session is
/// finished; a small one goes up with one request.
/// </summary>
public sealed class DropboxStore : CloudStore
{
    internal const string ApiBase = "https://api.dropboxapi.com/2/";
    internal const string ContentBase = "https://content.dropboxapi.com/2/";

    public DropboxStore(TaskDestination destination, CloudAccount account, SpeedLimiter? limiter = null)
        : base(destination, account, limiter)
    {
    }

    public override string Kind => DestinationKinds.Dropbox;

    public override string AccountName()
    {
        using var document = Rpc("users/get_current_account", null, null);
        var root = document.RootElement;
        return Text(root, "email")
            ?? (root.TryGetProperty("name", out var name) ? Text(name, "display_name") : null)
            ?? ServiceName;
    }

    public override void CreateFolder(string path)
    {
        if (path.Length == 0 || IsKnownFolder(path))
        {
            return;
        }

        // Dropbox makes the folders above by itself.
        using var response = Send(() => RpcRequest("files/create_folder_v2", new JsonObject { ["path"] = ApiPath(path), ["autorename"] = false }), path);
        if (!response.IsSuccessStatusCode && !(response.StatusCode == HttpStatusCode.Conflict && Summary(response).Contains("conflict/folder", StringComparison.Ordinal)))
        {
            throw Failure(response, path);
        }

        RememberFolder(path);
    }

    public override bool FolderExists(string path) => path.Length == 0 || IsKnownFolder(path) || Metadata(path) is { IsFolder: true };

    public override bool FileExists(string path) => Metadata(path) is { IsFolder: false };

    public override List<StoreEntry> List(string folder)
    {
        var result = new List<StoreEntry>();
        JsonDocument? page;
        try
        {
            page = Rpc("files/list_folder", new JsonObject { ["path"] = ApiPath(folder), ["recursive"] = false, ["limit"] = 2000 }, folder);
        }
        catch (FileNotFoundException)
        {
            return result;
        }

        while (page is not null)
        {
            var current = page;
            using (current)
            {
                var root = current.RootElement;
                if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in entries.EnumerateArray())
                    {
                        var item = Item.From(entry);
                        if (item is not null && item.Name.Length > 0)
                        {
                            result.Add(new StoreEntry(Combine(folder, item.Name), item.Name, item.IsFolder, item.Size, item.ModifiedUtc));
                        }
                    }
                }

                string? cursor = root.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True ? Text(root, "cursor") : null;
                page = cursor is null ? null : Rpc("files/list_folder/continue", new JsonObject { ["cursor"] = cursor }, folder);
            }
        }

        return result;
    }

    public override void DeleteFolder(string path)
    {
        ForgetFolders();
        Delete(path);
    }

    public override bool RemoveEmptyFolder(string path)
    {
        ForgetFolders();
        if (Metadata(path) is null)
        {
            return true;
        }

        using (var page = Rpc("files/list_folder", new JsonObject { ["path"] = ApiPath(path), ["recursive"] = false, ["limit"] = 1 }, path))
        {
            if (page.RootElement.TryGetProperty("entries", out var entries) && entries.GetArrayLength() > 0)
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
        Move(source, target);
    }

    public override long GetFileSize(string path) => Metadata(path) is { IsFolder: false } item ? item.Size : throw new FileNotFoundException(null, path);

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

    public override void MoveFile(string source, string target)
    {
        if (FileExists(target))
        {
            Delete(target);
        }

        Move(source, target);
    }

    public override StoreSpace GetSpace(string path)
    {
        try
        {
            using var document = Rpc("users/get_space_usage", null, path);
            var root = document.RootElement;
            long used = Number(root, "used");
            long allocated = -1;
            if (root.TryGetProperty("allocation", out var allocation))
            {
                allocated = Number(allocation, "allocated");
                if (Text(allocation, ".tag") == "team" && Number(allocation, "used") is >= 0 and var teamUsed)
                {
                    used = teamUsed;
                }
            }

            return allocated > 0 && used >= 0 ? new StoreSpace(allocated, Math.Max(0, allocated - used)) : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    protected override Stream OpenAt(string path, long offset, long length)
    {
        var response = Send(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, ContentBase + "files/download");
                request.Headers.Add("Dropbox-API-Arg", Argument(new JsonObject { ["path"] = ApiPath(path) }));
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

    protected override Exception Failure(HttpResponseMessage response, string? path)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            // Dropbox tells what went wrong in "error_summary": "path/not_found/..", "path/insufficient_space/..".
            string summary = Summary(response);
            string message = Messages.Format(Messages.StoreHttpFailed, ServiceName, "409 " + summary, path ?? Root);
            if (summary.Contains("not_found", StringComparison.Ordinal))
            {
                return new FileNotFoundException(message, path);
            }

            if (summary.Contains("insufficient_space", StringComparison.Ordinal))
            {
                return new StoreException(Messages.Format(Messages.StoreFull, ServiceName));
            }

            if (summary.Contains("no_write_permission", StringComparison.Ordinal))
            {
                return new UnauthorizedAccessException(message);
            }

            if (summary.Contains("too_large", StringComparison.Ordinal))
            {
                return new StoreException(Messages.Format(Messages.CloudFileTooLarge, ServiceName, path ?? Root));
            }

            return new StoreException(message);
        }

        return base.Failure(response, path);
    }

    /// <summary>The path for Dropbox: "/" + the path in the app folder; the app folder itself is "".</summary>
    private static string ApiPath(string path) => path.Length == 0 ? string.Empty : "/" + path;

    /// <summary>
    /// The argument of a request in the header «Dropbox-API-Arg»: JSON where every character beyond ASCII is escaped,
    /// since a header carries ASCII only.
    /// </summary>
    private static string Argument(JsonObject argument) => argument.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default });

    /// <summary>The "error_summary" of an answer (read once and kept).</summary>
    private static string Summary(HttpResponseMessage response)
    {
        response.Content.LoadIntoBufferAsync().GetAwaiter().GetResult();
        try
        {
            using var document = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return Text(document.RootElement, "error_summary") ?? string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>The "correct_offset" an upload error names, wherever it is in the error; -1 if none.</summary>
    private static long CorrectOffset(HttpResponseMessage response)
    {
        response.Content.LoadIntoBufferAsync().GetAwaiter().GetResult();
        try
        {
            using var document = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return Find(document.RootElement);
        }
        catch (JsonException)
        {
            return -1;
        }

        static long Find(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return -1;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (property.Name == "correct_offset" && property.Value.TryGetInt64(out long offset))
                {
                    return offset;
                }

                if (Find(property.Value) is >= 0 and var found)
                {
                    return found;
                }
            }

            return -1;
        }
    }

    private static HttpRequestMessage RpcRequest(string endpoint, JsonObject? argument)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ApiBase + endpoint);
        if (argument is not null)
        {
            request.Content = JsonBody(argument);
        }

        return request;
    }

    private JsonDocument Rpc(string endpoint, JsonObject? argument, string? path) => SendJson(() => RpcRequest(endpoint, argument), path);

    /// <summary>A file or folder; null if nothing is there.</summary>
    private Item? Metadata(string path)
    {
        if (path.Length == 0)
        {
            return new Item(string.Empty, true, 0, DateTime.MinValue);
        }

        try
        {
            using var document = Rpc("files/get_metadata", new JsonObject { ["path"] = ApiPath(path) }, path);
            return Item.From(document.RootElement);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private void Delete(string path)
    {
        try
        {
            using var document = Rpc("files/delete_v2", new JsonObject { ["path"] = ApiPath(path) }, path);
        }
        catch (FileNotFoundException)
        {
            // Not there: nothing to do.
        }
    }

    private void Move(string source, string target)
    {
        using var document = Rpc("files/move_v2", new JsonObject { ["from_path"] = ApiPath(source), ["to_path"] = ApiPath(target), ["autorename"] = false }, source);
    }

    private sealed record Item(string Name, bool IsFolder, long Size, DateTime ModifiedUtc)
    {
        public static Item? From(JsonElement entry)
        {
            string? tag = Text(entry, ".tag");
            if (tag is not ("file" or "folder"))
            {
                return null;
            }

            return new Item(Text(entry, "name") ?? string.Empty, tag == "folder", Math.Max(0, Number(entry, "size")), Time(entry, "server_modified"));
        }
    }

    /// <summary>
    /// A file being uploaded: a small one with <c>files/upload</c>, a larger one with an upload session whose chunks
    /// are sent again from the offset Dropbox says it has.
    /// </summary>
    private sealed class Writer(DropboxStore store, string path) : ChunkedWriter(store, path)
    {
        private readonly DropboxStore _store = store;
        private string? _session;

        protected override void SendChunk(ArraySegment<byte> data, bool last, long total)
        {
            if (_session is null && last)
            {
                using (Upload("files/upload", CommitArgument(), data))
                {
                    Sent = data.Count;
                }

                return;
            }

            if (_session is null)
            {
                using var document = Upload("files/upload_session/start", new JsonObject { ["close"] = false }, data);
                _session = Text(document.RootElement, "session_id") ?? throw new StoreException(Messages.StoreUploadRefused);
                Started = true;
                Sent = data.Count;
                return;
            }

            int offset = 0;
            for (int attempt = 1; ; attempt++)
            {
                var piece = data[offset..];
                var cursor = new JsonObject { ["session_id"] = _session, ["offset"] = Sent + offset };
                string endpoint = last ? "files/upload_session/finish" : "files/upload_session/append_v2";
                JsonObject argument = last ? new JsonObject { ["cursor"] = cursor, ["commit"] = CommitArgument() } : new JsonObject { ["cursor"] = cursor, ["close"] = false };
                using var response = _store.Send(() => Request(endpoint, argument, piece), Path, _store.TransferTime(piece.Count));
                if (response.IsSuccessStatusCode)
                {
                    Sent += data.Count;
                    return;
                }

                // A chunk sent again after a broken connection may have arrived the first time: Dropbox says where it
                // stands, and only the rest is sent.
                long correct = response.StatusCode == HttpStatusCode.Conflict ? CorrectOffset(response) : -1;
                if (last && correct < 0 && response.StatusCode == HttpStatusCode.Conflict
                    && _store.Metadata(Path) is { IsFolder: false } done && done.Size == total)
                {
                    // The finish of an earlier attempt arrived, only its answer was lost: the session is closed and
                    // the file is there.
                    Sent = total;
                    return;
                }

                if (correct < 0 || attempt >= UploadAttempts)
                {
                    throw _store.Failure(response, Path);
                }

                long arrived = correct - Sent;
                if (arrived < 0 || arrived > data.Count)
                {
                    throw new StoreException(Messages.Format(Messages.CloudUploadLost, _store.ServiceName, Path));
                }

                if (arrived == data.Count && !last)
                {
                    Sent += data.Count;
                    return;
                }

                offset = (int)arrived;
            }
        }

        protected override void GiveUp()
        {
            // An unfinished session is dropped by Dropbox after a week; nothing is in the folder.
        }

        protected override void Remove() => _store.Delete(Path);

        private JsonObject CommitArgument() => new() { ["path"] = ApiPath(Path), ["mode"] = "overwrite", ["autorename"] = false, ["mute"] = true };

        private HttpRequestMessage Request(string endpoint, JsonObject argument, ArraySegment<byte> data)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, ContentBase + endpoint) { Content = _store.Body(data) };
            request.Headers.Add("Dropbox-API-Arg", Argument(argument));
            return request;
        }

        private JsonDocument Upload(string endpoint, JsonObject argument, ArraySegment<byte> data) =>
            _store.SendJson(() => Request(endpoint, argument, data), Path, _store.TransferTime(data.Count));
    }
}
