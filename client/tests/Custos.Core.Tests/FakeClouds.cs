using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Custos.Core.Cloud;

namespace Custos.Core.Tests;

/// <summary>
/// In-memory stand-ins for the REST interfaces of Google Drive, Yandex Disk and Dropbox (client stage 4b): the
/// requests CUSTOS makes, answered the way the services document them. Each test gets its own drive. Real services
/// are checked by hand (no tokens in CI).
/// </summary>
internal abstract class FakeCloud : HttpMessageHandler
{
    private int _tokens;

    protected FakeCloud()
    {
        Token = NewToken();
    }

    /// <summary>The access token the drive takes now.</summary>
    public string Token { get; private set; }

    /// <summary>Requests that reached the drive.</summary>
    public int Requests { get; private set; }

    /// <summary>
    /// Trouble before a request is handled: an answer to give instead (429, 500), or an exception to throw (a broken
    /// connection). Null = the request is handled.
    /// </summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Trouble { get; set; }

    /// <summary>
    /// Uploads to break: after taking this many bytes of a chunk, the connection "breaks" (the bytes are kept, the
    /// answer is lost). Each break is used once.
    /// </summary>
    public Queue<long> Breaks { get; } = new();

    /// <summary>The answer to the next upload chunk is lost although the chunk arrived whole.</summary>
    public int LoseAnswers { get; set; }

    public long Total { get; set; } = 15L * 1024 * 1024 * 1024;

    public long Used { get; set; } = 1_000_000;

    protected Lock Sync { get; } = new();

    /// <summary>A new token, as after a refresh: the one before no longer works.</summary>
    public string NewToken()
    {
        Token = "token-" + Interlocked.Increment(ref _tokens).ToString(CultureInfo.InvariantCulture);
        return Token;
    }

    /// <summary>The account in a store: its tokens come from this drive.</summary>
    public CloudAccount Account(string user = "backup@example.org") =>
        new(user, new FakeTokens(this)) { Handler = this, RetryUnit = TimeSpan.FromMilliseconds(1) };

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        lock (Sync)
        {
            Requests++;
        }

        if (Trouble?.Invoke(request) is { } trouble)
        {
            return trouble;
        }

        if (NeedsToken(request.RequestUri!) && request.Headers.Authorization?.Parameter != Token)
        {
            return Json(HttpStatusCode.Unauthorized, new JsonObject { ["error"] = "invalid_token", ["message"] = "Unauthorized" });
        }

        return Handle(request, cancellationToken);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(Send(request, cancellationToken));

    protected abstract HttpResponseMessage Handle(HttpRequestMessage request, CancellationToken cancellationToken);

    /// <summary>Addresses that want the token; download and upload links of Yandex Disk do not.</summary>
    protected virtual bool NeedsToken(Uri address) => true;

    /// <summary>The body of a request; a break in <see cref="Breaks"/> keeps only its first bytes and throws.</summary>
    protected byte[] Body(HttpRequestMessage request, bool upload, Action<byte[]>? kept = null)
    {
        if (request.Content is null)
        {
            return [];
        }

        using var stream = request.Content.ReadAsStream();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        byte[] data = copy.ToArray();
        if (upload)
        {
            long? cut = null;
            lock (Sync)
            {
                if (Breaks.Count > 0 && data.Length > 0)
                {
                    cut = Math.Min(Breaks.Dequeue(), data.Length);
                }
            }

            if (cut is { } at)
            {
                kept?.Invoke(data[..(int)at]);
                throw new HttpRequestException("The connection was reset (test).");
            }
        }

        return data;
    }

    /// <summary>Whether to lose the answer of an upload that arrived.</summary>
    protected bool LoseAnswer()
    {
        lock (Sync)
        {
            if (LoseAnswers > 0)
            {
                LoseAnswers--;
                return true;
            }
        }

        return false;
    }

    protected static HttpResponseMessage Json(HttpStatusCode status, JsonNode? body)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body?.ToJsonString() ?? "null", Encoding.UTF8, "application/json"),
        };
        return response;
    }

    protected static HttpResponseMessage Empty(HttpStatusCode status) => new(status) { Content = new ByteArrayContent([]) };

    /// <summary>A file or a part of it, as a download answers a Range header.</summary>
    protected static HttpResponseMessage Download(HttpRequestMessage request, byte[] data)
    {
        long from = request.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0;
        if (from > data.Length)
        {
            return Empty(HttpStatusCode.RequestedRangeNotSatisfiable);
        }

        var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(data, (int)from, data.Length - (int)from),
        };
        return response;
    }

    protected static Dictionary<string, string> Query(Uri address)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in address.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            result[Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals])] = equals < 0 ? string.Empty : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
        }

        return result;
    }

    protected static JsonNode? ReadJson(byte[] body) => body.Length == 0 ? null : JsonNode.Parse(body);

    protected static string Time(DateTime time) => time.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    /// <summary>The tokens of an account of a fake drive: a renewal makes a new token, as a refresh would.</summary>
    private sealed class FakeTokens(FakeCloud cloud) : ICloudTokens
    {
        private string? _current;

        public string AccessToken(bool renew, CancellationToken cancellationToken)
        {
            if (cloud.Revoked)
            {
                throw new CloudAccessRevokedException(Messages.Format(Messages.CloudAccessRevoked, "backup@example.org", "test"));
            }

            if (renew || _current is null)
            {
                _current = cloud.Token;
            }

            return _current;
        }
    }

    /// <summary>The access was revoked: the token stops working and cannot be renewed.</summary>
    public bool Revoked { get; set; }
}

/// <summary>Google Drive API v3: files by id in folders by id, resumable uploads, Range downloads.</summary>
internal sealed partial class FakeGoogleDrive : FakeCloud
{
    private const string FolderType = "application/vnd.google-apps.folder";
    private readonly Dictionary<string, Item> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private int _ids;

    /// <summary>Files and folders by id, for the tests to look at.</summary>
    public IReadOnlyCollection<Item> Items => _items.Values;

    /// <summary>Resumable uploads that were started.</summary>
    public int Sessions { get; private set; }

    protected override HttpResponseMessage Handle(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var address = request.RequestUri!;
        string path = address.AbsolutePath;
        var query = Query(address);
        if (path == "/upload/drive/v3/files")
        {
            return query.GetValueOrDefault("uploadType") switch
            {
                "multipart" when request.Method == HttpMethod.Post => Multipart(request),
                "resumable" when request.Method == HttpMethod.Post => StartSession(request),
                "resumable" when request.Method == HttpMethod.Put => Chunk(request, query["upload_id"]),
                "resumable" when request.Method == HttpMethod.Delete => Cancel(query["upload_id"]),
                _ => Empty(HttpStatusCode.BadRequest),
            };
        }

        lock (Sync)
        {
            if (path == "/drive/v3/about")
            {
                return Json(HttpStatusCode.OK, new JsonObject
                {
                    ["user"] = new JsonObject { ["emailAddress"] = "backup@example.org", ["displayName"] = "Backup" },
                    ["storageQuota"] = new JsonObject { ["limit"] = Total.ToString(CultureInfo.InvariantCulture), ["usage"] = Used.ToString(CultureInfo.InvariantCulture) },
                });
            }

            if (path == "/drive/v3/files" && request.Method == HttpMethod.Get)
            {
                return ListFiles(query);
            }

            if (path == "/drive/v3/files" && request.Method == HttpMethod.Post)
            {
                var meta = ReadJson(Body(request, upload: false))!;
                string parent = meta["parents"]![0]!.GetValue<string>();
                if (parent != "root" && !_items.ContainsKey(parent))
                {
                    return NotFound(parent);
                }

                var folder = Add(meta["name"]!.GetValue<string>(), parent, meta["mimeType"]?.GetValue<string>() == FolderType, []);
                return Json(HttpStatusCode.OK, new JsonObject { ["id"] = folder.Id });
            }

            if (path.StartsWith("/drive/v3/files/", StringComparison.Ordinal))
            {
                string id = Uri.UnescapeDataString(path["/drive/v3/files/".Length..]);
                if (!_items.TryGetValue(id, out var item))
                {
                    return NotFound(id);
                }

                if (request.Method == HttpMethod.Delete)
                {
                    Remove(id);
                    return Empty(HttpStatusCode.NoContent);
                }

                if (request.Method == HttpMethod.Patch)
                {
                    var meta = ReadJson(Body(request, upload: false));
                    if (meta?["name"]?.GetValue<string>() is { } name)
                    {
                        item.Name = name;
                    }

                    if (query.TryGetValue("addParents", out string? add))
                    {
                        Assert.Equal(item.Parent, query["removeParents"]);
                        item.Parent = add;
                    }

                    return Json(HttpStatusCode.OK, new JsonObject { ["id"] = id });
                }

                if (request.Method == HttpMethod.Get && query.GetValueOrDefault("alt") == "media")
                {
                    return Download(request, item.Data);
                }
            }
        }

        return Empty(HttpStatusCode.BadRequest);
    }

    private static HttpResponseMessage NotFound(string id) =>
        Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = new JsonObject { ["code"] = 404, ["message"] = "File not found: " + id } });

    [GeneratedRegex(@"^'((?:[^'\\]|\\.)*)' in parents and trashed = false(?: and name = '((?:[^'\\]|\\.)*)')?$")]
    private static partial Regex ChildrenQuery();

    private static string Unquote(string text) => Regex.Replace(text, @"\\(.)", "$1");

    private HttpResponseMessage ListFiles(Dictionary<string, string> query)
    {
        var match = ChildrenQuery().Match(query["q"]);
        Assert.True(match.Success, "Unexpected query: " + query["q"]);
        Assert.Equal("drive", query["spaces"]);
        string parent = Unquote(match.Groups[1].Value);
        string? name = match.Groups[2].Success ? Unquote(match.Groups[2].Value) : null;
        int size = int.Parse(query["pageSize"], CultureInfo.InvariantCulture);
        int start = query.TryGetValue("pageToken", out string? token) ? int.Parse(token, CultureInfo.InvariantCulture) : 0;
        var all = _items.Values.Where(i => i.Parent == parent && (name is null || i.Name == name)).OrderBy(i => i.Number).ToList();
        var files = new JsonArray();
        foreach (var item in all.Skip(start).Take(size))
        {
            var file = new JsonObject
            {
                ["id"] = item.Id,
                ["name"] = item.Name,
                ["mimeType"] = item.IsFolder ? FolderType : "application/octet-stream",
                ["modifiedTime"] = Time(item.Modified),
            };
            if (!item.IsFolder)
            {
                file["size"] = item.Data.Length.ToString(CultureInfo.InvariantCulture);
            }

            files.Add(file);
        }

        var answer = new JsonObject { ["files"] = files };
        if (start + size < all.Count)
        {
            answer["nextPageToken"] = (start + size).ToString(CultureInfo.InvariantCulture);
        }

        return Json(HttpStatusCode.OK, answer);
    }

    private HttpResponseMessage Multipart(HttpRequestMessage request)
    {
        var parts = ((MultipartContent)request.Content!).ToList();
        Assert.Equal(2, parts.Count);
        var meta = JsonNode.Parse(parts[0].ReadAsStream())!;
        using var copy = new MemoryStream();
        parts[1].ReadAsStream().CopyTo(copy);
        lock (Sync)
        {
            string parent = meta["parents"]![0]!.GetValue<string>();
            if (!_items.ContainsKey(parent))
            {
                return NotFound(parent);
            }

            var file = Add(meta["name"]!.GetValue<string>(), parent, false, copy.ToArray());
            return Json(HttpStatusCode.OK, new JsonObject { ["id"] = file.Id });
        }
    }

    private HttpResponseMessage StartSession(HttpRequestMessage request)
    {
        var meta = ReadJson(Body(request, upload: false))!;
        Assert.Equal("application/octet-stream", request.Headers.GetValues("X-Upload-Content-Type").Single());
        lock (Sync)
        {
            string id = "s" + (++_ids).ToString(CultureInfo.InvariantCulture);
            _sessions[id] = new Session(meta["name"]!.GetValue<string>(), meta["parents"]![0]!.GetValue<string>());
            Sessions++;
            var response = Empty(HttpStatusCode.OK);
            response.Headers.Location = new Uri("https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&upload_id=" + id);
            return response;
        }
    }

    private HttpResponseMessage Chunk(HttpRequestMessage request, string id)
    {
        Session? session;
        lock (Sync)
        {
            if (!_sessions.TryGetValue(id, out session))
            {
                return NotFound(id);
            }
        }

        string range = request.Content!.Headers.GetValues("Content-Range").Single();
        var match = Regex.Match(range, @"^bytes (?:(\d+)-(\d+)|\*)/(\d+|\*)$");
        Assert.True(match.Success, "Content-Range: " + range);
        long? total = match.Groups[3].Value == "*" ? null : long.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        byte[] data = Body(request, upload: true, kept: part =>
        {
            lock (Sync)
            {
                // What arrived before the connection broke is kept, as Google does.
                if (long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) == session.Data.Length)
                {
                    session.Data.Write(part);
                }
            }
        });
        lock (Sync)
        {
            if (session.FileId is { } done)
            {
                // A finished upload answers with its file, as long as it is asked.
                return Json(HttpStatusCode.OK, new JsonObject { ["id"] = done });
            }

            if (match.Groups[1].Success)
            {
                long from = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                long to = long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                Assert.Equal(to - from + 1, data.Length);
                Assert.Equal(session.Data.Length, from);
                session.Data.Write(data);
            }

            if (total is { } length && session.Data.Length == length)
            {
                var file = Add(session.Name, session.Parent, false, session.Data.ToArray());
                session.FileId = file.Id;
                return LoseAnswer() ? throw new HttpRequestException("Answer lost (test).") : Json(HttpStatusCode.OK, new JsonObject { ["id"] = file.Id });
            }

            if (LoseAnswer())
            {
                throw new HttpRequestException("Answer lost (test).");
            }

            var response = Empty((HttpStatusCode)308);
            if (session.Data.Length > 0)
            {
                response.Headers.TryAddWithoutValidation("Range", "bytes=0-" + (session.Data.Length - 1).ToString(CultureInfo.InvariantCulture));
            }

            return response;
        }
    }

    private HttpResponseMessage Cancel(string id)
    {
        lock (Sync)
        {
            _sessions.Remove(id);
        }

        return Empty((HttpStatusCode)499);
    }

    private Item Add(string name, string parent, bool folder, byte[] data)
    {
        var item = new Item("i" + (++_ids).ToString(CultureInfo.InvariantCulture), name, parent, folder, data, _ids);
        _items[item.Id] = item;
        return item;
    }

    private void Remove(string id)
    {
        foreach (var child in _items.Values.Where(i => i.Parent == id).Select(i => i.Id).ToList())
        {
            Remove(child);
        }

        _items.Remove(id);
    }

    public sealed class Item(string id, string name, string parent, bool folder, byte[] data, int number)
    {
        public string Id { get; } = id;

        public string Name { get; set; } = name;

        public string Parent { get; set; } = parent;

        public bool IsFolder { get; } = folder;

        public byte[] Data { get; } = data;

        public int Number { get; } = number;

        public DateTime Modified { get; } = DateTime.UtcNow;
    }

    private sealed class Session(string name, string parent)
    {
        public string Name { get; } = name;

        public string Parent { get; } = parent;

        public MemoryStream Data { get; } = new();

        public string? FileId { get; set; }
    }
}

/// <summary>Dropbox API v2: paths in the app folder, RPC and content endpoints, upload sessions with offsets.</summary>
internal sealed class FakeDropbox : FakeCloud
{
    private readonly Dictionary<string, Item> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MemoryStream> _sessions = new(StringComparer.Ordinal);
    private int _ids;

    /// <summary>Upload sessions that were started.</summary>
    public int Sessions { get; private set; }

    public bool HasFile(string path) => _items.TryGetValue(path, out var item) && !item.IsFolder;

    protected override HttpResponseMessage Handle(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var address = request.RequestUri!;
        Assert.Equal(HttpMethod.Post, request.Method);
        if (address.Host == "content.dropboxapi.com")
        {
            string raw = request.Headers.GetValues("Dropbox-API-Arg").Single();
            Assert.True(raw.All(c => c < 128), "Dropbox-API-Arg must be ASCII: " + raw);
            return Content(address.AbsolutePath["/2/".Length..], JsonNode.Parse(raw)!, request);
        }

        Assert.Equal("api.dropboxapi.com", address.Host);
        var argument = ReadJson(Body(request, upload: false));
        lock (Sync)
        {
            return Rpc(address.AbsolutePath["/2/".Length..], argument);
        }
    }

    private static HttpResponseMessage Error(string summary, JsonObject? error = null) =>
        Json(HttpStatusCode.Conflict, new JsonObject { ["error_summary"] = summary, ["error"] = error ?? new JsonObject() });

    private static string Parent(string path) => path.LastIndexOf('/') is var slash and > 0 ? path[..slash] : string.Empty;

    private HttpResponseMessage Rpc(string endpoint, JsonNode? argument)
    {
        string Path(string name) => argument![name]!.GetValue<string>();
        switch (endpoint)
        {
            case "users/get_current_account":
                return Json(HttpStatusCode.OK, new JsonObject { ["email"] = "backup@example.org", ["name"] = new JsonObject { ["display_name"] = "Backup" } });
            case "users/get_space_usage":
                return Json(HttpStatusCode.OK, new JsonObject { ["used"] = Used, ["allocation"] = new JsonObject { [".tag"] = "individual", ["allocated"] = Total } });
            case "files/create_folder_v2":
                if (_items.ContainsKey(Path("path")))
                {
                    return Error("path/conflict/folder/..");
                }

                MakeFolders(Path("path"));
                return Json(HttpStatusCode.OK, new JsonObject { ["metadata"] = Metadata(_items[Path("path")]) });
            case "files/get_metadata":
                return _items.TryGetValue(Path("path"), out var found) ? Json(HttpStatusCode.OK, Metadata(found)) : Error("path/not_found/..");
            case "files/list_folder":
            {
                string folder = Path("path");
                if (folder.Length > 0 && !(_items.TryGetValue(folder, out var item) && item.IsFolder))
                {
                    return Error("path/not_found/..");
                }

                int limit = argument!["limit"]?.GetValue<int>() ?? 2000;
                return Page(folder, 0, limit);
            }

            case "files/list_folder/continue":
            {
                string[] cursor = Path("cursor").Split('|');
                return Page(cursor[0], int.Parse(cursor[1], CultureInfo.InvariantCulture), int.Parse(cursor[2], CultureInfo.InvariantCulture));
            }

            case "files/delete_v2":
            {
                string path = Path("path");
                if (!_items.TryGetValue(path, out var item))
                {
                    return Error("path_lookup/not_found/..");
                }

                foreach (string key in _items.Keys.Where(k => k.Equals(path, StringComparison.OrdinalIgnoreCase) || k.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    _items.Remove(key);
                }

                return Json(HttpStatusCode.OK, new JsonObject { ["metadata"] = Metadata(item) });
            }

            case "files/move_v2":
            {
                string from = Path("from_path");
                string to = Path("to_path");
                if (!_items.ContainsKey(from))
                {
                    return Error("from_lookup/not_found/..");
                }

                if (_items.ContainsKey(to))
                {
                    return Error("to/conflict/file/..");
                }

                MakeFolders(Parent(to));
                foreach (var pair in _items.Where(p => p.Key.Equals(from, StringComparison.OrdinalIgnoreCase) || p.Key.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    _items.Remove(pair.Key);
                    string moved = to + pair.Value.Path[from.Length..];
                    _items[moved] = pair.Value with { Path = moved };
                }

                return Json(HttpStatusCode.OK, new JsonObject { ["metadata"] = Metadata(_items[to]) });
            }

            default:
                return Empty(HttpStatusCode.BadRequest);
        }
    }

    private HttpResponseMessage Content(string endpoint, JsonNode argument, HttpRequestMessage request)
    {
        if (endpoint == "files/download")
        {
            lock (Sync)
            {
                return _items.TryGetValue(argument["path"]!.GetValue<string>(), out var file) && !file.IsFolder
                    ? Download(request, file.Data)
                    : Error("path/not_found/..");
            }
        }

        string? session = argument["cursor"]?["session_id"]?.GetValue<string>();
        long offset = argument["cursor"]?["offset"]?.GetValue<long>() ?? 0;
        MemoryStream? open = null;
        lock (Sync)
        {
            if (session is not null && !_sessions.TryGetValue(session, out open))
            {
                return Error("lookup_failed/not_found/..");
            }

            if (open is not null && open.Length != offset)
            {
                return Error("lookup_failed/incorrect_offset/..", new JsonObject
                {
                    [".tag"] = "lookup_failed",
                    ["lookup_failed"] = new JsonObject { [".tag"] = "incorrect_offset", ["correct_offset"] = open.Length },
                });
            }
        }

        byte[] data = Body(request, upload: true, kept: part =>
        {
            lock (Sync)
            {
                open?.Write(part);
            }
        });
        lock (Sync)
        {
            HttpResponseMessage answer;
            switch (endpoint)
            {
                case "files/upload":
                    answer = Json(HttpStatusCode.OK, Commit(argument, data));
                    break;
                case "files/upload_session/start":
                    string id = "u" + (++_ids).ToString(CultureInfo.InvariantCulture);
                    _sessions[id] = new MemoryStream();
                    _sessions[id].Write(data);
                    Sessions++;
                    answer = Json(HttpStatusCode.OK, new JsonObject { ["session_id"] = id });
                    break;
                case "files/upload_session/append_v2":
                    open!.Write(data);
                    answer = Json(HttpStatusCode.OK, null);
                    break;
                case "files/upload_session/finish":
                    open!.Write(data);
                    _sessions.Remove(session!);
                    answer = Json(HttpStatusCode.OK, Commit(argument["commit"]!, open.ToArray()));
                    break;
                default:
                    return Empty(HttpStatusCode.BadRequest);
            }

            if (endpoint != "files/upload_session/start" && LoseAnswer())
            {
                answer.Dispose();
                throw new HttpRequestException("Answer lost (test).");
            }

            return answer;
        }
    }

    private JsonObject Commit(JsonNode commit, byte[] data)
    {
        Assert.Equal("overwrite", commit["mode"]!.GetValue<string>());
        string path = commit["path"]!.GetValue<string>();
        MakeFolders(Parent(path));
        var item = new Item(path, false, data, DateTime.UtcNow);
        _items[path] = item;
        return Metadata(item);
    }

    private void MakeFolders(string path)
    {
        if (path.Length == 0 || _items.ContainsKey(path))
        {
            return;
        }

        MakeFolders(Parent(path));
        _items[path] = new Item(path, true, [], DateTime.UtcNow);
    }

    private HttpResponseMessage Page(string folder, int start, int limit)
    {
        var children = _items.Values.Where(i => Parent(i.Path).Equals(folder, StringComparison.OrdinalIgnoreCase)).OrderBy(i => i.Path, StringComparer.Ordinal).ToList();
        var entries = new JsonArray();
        foreach (var item in children.Skip(start).Take(limit))
        {
            entries.Add(Metadata(item));
        }

        bool more = start + limit < children.Count;
        return Json(HttpStatusCode.OK, new JsonObject
        {
            ["entries"] = entries,
            ["has_more"] = more,
            ["cursor"] = folder + "|" + (start + limit).ToString(CultureInfo.InvariantCulture) + "|" + limit.ToString(CultureInfo.InvariantCulture),
        });
    }

    private static JsonObject Metadata(Item item)
    {
        var data = new JsonObject
        {
            [".tag"] = item.IsFolder ? "folder" : "file",
            ["name"] = item.Path[(item.Path.LastIndexOf('/') + 1)..],
            ["path_display"] = item.Path,
        };
        if (!item.IsFolder)
        {
            data["size"] = item.Data.Length;
            data["server_modified"] = item.Modified.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }

        return data;
    }

    private sealed record Item(string Path, bool IsFolder, byte[] Data, DateTime Modified);
}

/// <summary>Yandex Disk REST API: app:/ paths, upload and download links, operations that finish later.</summary>
internal sealed class FakeYandexDisk : FakeCloud
{
    private const string Uploader = "uploader.example.net";
    private const string Downloader = "downloader.example.net";
    private readonly Dictionary<string, Item> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _uploads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _operations = new(StringComparer.Ordinal);
    private int _ids;

    /// <summary>A file larger than this is "processed" after the upload: the upload answers 202.</summary>
    public long ProcessedAbove { get; set; } = 4 * 1024 * 1024;

    /// <summary>The largest file the Disk takes (1 GB without a subscription); a larger one is refused with 413.</summary>
    public long LargestFile { get; set; } = long.MaxValue;

    public bool HasFile(string path) => _items.TryGetValue(path, out var item) && !item.IsFolder;

    protected override bool NeedsToken(Uri address) => address.Host == "cloud-api.yandex.net";

    protected override HttpResponseMessage Handle(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var address = request.RequestUri!;
        if (address.Host == Uploader)
        {
            return Upload(request, address.AbsolutePath.TrimStart('/'));
        }

        if (address.Host == Downloader)
        {
            lock (Sync)
            {
                string path = Uri.UnescapeDataString(address.AbsolutePath.TrimStart('/'));
                return _items.TryGetValue(path, out var file) && !file.IsFolder ? Download(request, file.Data) : Error(HttpStatusCode.NotFound, "DiskNotFoundError");
            }
        }

        Assert.Equal("OAuth", request.Headers.Authorization!.Scheme);
        var query = Query(address);
        string endpoint = address.AbsolutePath["/v1/disk".Length..];
        lock (Sync)
        {
            if (endpoint == "/" && request.Method == HttpMethod.Get)
            {
                return Json(HttpStatusCode.OK, new JsonObject
                {
                    ["user"] = new JsonObject { ["login"] = "backup.office", ["display_name"] = "Backup" },
                    ["total_space"] = Total,
                    ["used_space"] = Used,
                });
            }

            if (endpoint.StartsWith("/operations/", StringComparison.Ordinal))
            {
                string id = endpoint["/operations/".Length..];
                int left = _operations[id];
                _operations[id] = left - 1;
                return Json(HttpStatusCode.OK, new JsonObject { ["status"] = left > 0 ? "in-progress" : "success" });
            }

            if (endpoint == "/resources/move")
            {
                return Move(Path(query["from"]), Path(query["path"]), query["overwrite"] == "true");
            }

            string path = Path(query["path"]);
            switch (endpoint)
            {
                case "/resources" when request.Method == HttpMethod.Get:
                    return Resource(path, query);
                case "/resources" when request.Method == HttpMethod.Put:
                    if (_items.ContainsKey(path))
                    {
                        return Error(HttpStatusCode.Conflict, "DiskPathPointsToExistentDirectoryError");
                    }

                    if (path.Length > 0 && !IsFolder(Parent(path)))
                    {
                        return Error(HttpStatusCode.Conflict, "DiskPathDoesntExistsError");
                    }

                    _items[path] = new Item(true, [], DateTime.UtcNow);
                    return Json(HttpStatusCode.Created, new JsonObject { ["href"] = "https://cloud-api.yandex.net/v1/disk/resources?path=app%3A%2F" });
                case "/resources" when request.Method == HttpMethod.Delete:
                    Assert.Equal("true", query["permanently"]);
                    if (!_items.TryGetValue(path, out var item))
                    {
                        return Error(HttpStatusCode.NotFound, "DiskNotFoundError");
                    }

                    RemoveTree(path);
                    return item.IsFolder ? Operation() : Empty(HttpStatusCode.NoContent);
                case "/resources/upload":
                    Assert.Equal("true", query["overwrite"]);
                    if (!IsFolder(Parent(path)))
                    {
                        return Error(HttpStatusCode.Conflict, "DiskPathDoesntExistsError");
                    }

                    string upload = "up" + (++_ids).ToString(CultureInfo.InvariantCulture);
                    _uploads[upload] = path;
                    return Json(HttpStatusCode.OK, new JsonObject { ["href"] = "https://" + Uploader + "/" + upload, ["method"] = "PUT", ["templated"] = false });
                case "/resources/download":
                    return _items.TryGetValue(path, out var file) && !file.IsFolder
                        ? Json(HttpStatusCode.OK, new JsonObject { ["href"] = "https://" + Downloader + "/" + Uri.EscapeDataString(path), ["method"] = "GET" })
                        : Error(HttpStatusCode.NotFound, "DiskNotFoundError");
                default:
                    return Empty(HttpStatusCode.BadRequest);
            }
        }
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string error) =>
        Json(status, new JsonObject { ["error"] = error, ["message"] = error, ["description"] = error });

    private static string Path(string apiPath)
    {
        Assert.StartsWith("app:/", apiPath, StringComparison.Ordinal);
        return apiPath["app:/".Length..].TrimEnd('/');
    }

    private static string Parent(string path) => path.LastIndexOf('/') is var slash and > 0 ? path[..slash] : string.Empty;

    private bool IsFolder(string path) => _items.TryGetValue(path, out var item) && item.IsFolder;

    private HttpResponseMessage Operation()
    {
        string id = "op" + (++_ids).ToString(CultureInfo.InvariantCulture);
        _operations[id] = 1;
        return Json(HttpStatusCode.Accepted, new JsonObject { ["href"] = "https://cloud-api.yandex.net/v1/disk/operations/" + id, ["method"] = "GET" });
    }

    private HttpResponseMessage Resource(string path, Dictionary<string, string> query)
    {
        if (!_items.TryGetValue(path, out var item))
        {
            return Error(HttpStatusCode.NotFound, "DiskNotFoundError");
        }

        var answer = new JsonObject { ["name"] = path[(path.LastIndexOf('/') + 1)..], ["type"] = item.IsFolder ? "dir" : "file", ["modified"] = Time(item.Modified) };
        if (!item.IsFolder)
        {
            answer["size"] = item.Data.Length;
            return Json(HttpStatusCode.OK, answer);
        }

        int limit = int.Parse(query.GetValueOrDefault("limit", "20"), CultureInfo.InvariantCulture);
        int offset = int.Parse(query.GetValueOrDefault("offset", "0"), CultureInfo.InvariantCulture);
        var children = _items.Where(p => p.Key.Length > 0 && Parent(p.Key) == path && p.Key != path).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        var items = new JsonArray();
        foreach (var (key, child) in children.Skip(offset).Take(limit))
        {
            var entry = new JsonObject { ["name"] = key[(key.LastIndexOf('/') + 1)..], ["type"] = child.IsFolder ? "dir" : "file", ["modified"] = Time(child.Modified) };
            if (!child.IsFolder)
            {
                entry["size"] = child.Data.Length;
            }

            items.Add(entry);
        }

        answer["_embedded"] = new JsonObject { ["items"] = items, ["total"] = children.Count, ["limit"] = limit, ["offset"] = offset };
        return Json(HttpStatusCode.OK, answer);
    }

    private HttpResponseMessage Move(string from, string to, bool overwrite)
    {
        if (!_items.TryGetValue(from, out var item))
        {
            return Error(HttpStatusCode.NotFound, "DiskNotFoundError");
        }

        if (_items.ContainsKey(to) && !overwrite)
        {
            return Error(HttpStatusCode.Conflict, "DiskResourceAlreadyExistsError");
        }

        if (!IsFolder(Parent(to)))
        {
            return Error(HttpStatusCode.Conflict, "DiskPathDoesntExistsError");
        }

        RemoveTree(to);
        foreach (var pair in _items.Where(p => p.Key == from || p.Key.StartsWith(from + "/", StringComparison.Ordinal)).ToList())
        {
            _items.Remove(pair.Key);
            _items[to + pair.Key[from.Length..]] = pair.Value;
        }

        return item.IsFolder ? Operation() : Json(HttpStatusCode.Created, new JsonObject { ["href"] = "https://cloud-api.yandex.net/v1/disk/resources" });
    }

    private void RemoveTree(string path)
    {
        foreach (string key in _items.Keys.Where(k => k == path || k.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
        {
            _items.Remove(key);
        }
    }

    private HttpResponseMessage Upload(HttpRequestMessage request, string id)
    {
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Null(request.Headers.Authorization);
        string path;
        lock (Sync)
        {
            if (!_uploads.Remove(id, out path!))
            {
                return Error(HttpStatusCode.NotFound, "UploadNotFound");
            }
        }

        // A broken upload keeps nothing: there is no way to go on with it.
        byte[] data = Body(request, upload: true);
        lock (Sync)
        {
            if (data.Length > LargestFile)
            {
                return Error(HttpStatusCode.RequestEntityTooLarge, "FileTooBig");
            }

            _items[path] = new Item(false, data, DateTime.UtcNow);
            return Empty(data.Length > ProcessedAbove ? HttpStatusCode.Accepted : HttpStatusCode.Created);
        }
    }

    private sealed record Item(bool IsFolder, byte[] Data, DateTime Modified);
}
