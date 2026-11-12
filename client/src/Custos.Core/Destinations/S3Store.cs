using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Custos.Core.Model;

namespace Custos.Core.Destinations;

/// <summary>
/// A bucket of Amazon S3 or of a server that speaks its language (MinIO, Ceph, a cloud provider), section 6.3.
/// There are no folders, only names with "/" in them, and nothing can be renamed: a file appears under its name when
/// its upload is complete, and a set is complete when its manifest, written last, is there.
/// </summary>
/// <remarks>
/// With Object Lock every file is uploaded with the time until which it cannot be deleted. Deleting removes every
/// version of an object where the bucket keeps versions, otherwise the data would stay behind a delete marker.
/// </remarks>
public sealed class S3Store : RemoteStore
{
    /// <summary>What S3 takes in one request; larger files go up in parts.</summary>
    private const int FirstPartSize = 16 * 1024 * 1024;
    private const long CopyLimit = 5L * 1024 * 1024 * 1024;
    private const long CopyPartSize = 1024L * 1024 * 1024;
    private const int DeleteBatch = 1000;

    /// <summary>The slowest upload that is still an upload and not a dead connection, in bytes per second.</summary>
    private const long SlowestUpload = 32 * 1024;

    private readonly AmazonS3Client _client;
    private readonly PinnedHttp _http;
    private readonly ServerTrust _trust;
    private readonly string _bucket;
    private readonly string _server;
    private readonly bool _https;
    private readonly StoreAccount _account;
    private readonly int _lockDays;
    private readonly ObjectLockMode _lockMode;
    private bool? _keepsVersions;
    private bool? _hasObjectLock;
    private bool _objectLockAsked;
    private string? _defaultRetention;
    private bool _singleDeletes;

    public S3Store(TaskDestination destination, StoreAccount account, SpeedLimiter? limiter = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _bucket = (destination.Bucket ?? string.Empty).Trim();
        _lockDays = Math.Max(0, destination.ObjectLockDays);
        _lockMode = destination.ObjectLockMode == ObjectLockModes.Compliance ? ObjectLockMode.Compliance : ObjectLockMode.Governance;
        _trust = new ServerTrust(destination.ServerFingerprint);
        _http = new PinnedHttp(_trust);
        Root = CleanRoot(destination.Path).Trim('/');
        Limiter = limiter;

        string region = string.IsNullOrWhiteSpace(destination.Region) ? "us-east-1" : destination.Region.Trim();
        var config = new AmazonS3Config
        {
            // Servers other than Amazon's often know neither the new checksums nor their trailers.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            HttpClientFactory = _http,
            MaxErrorRetry = 3,
        };
        string endpoint = (destination.Endpoint ?? string.Empty).Trim();
        if (endpoint.Length > 0)
        {
            if (!endpoint.Contains("://", StringComparison.Ordinal))
            {
                endpoint = "https://" + endpoint;
            }

            config.ServiceURL = endpoint.TrimEnd('/');
            config.AuthenticationRegion = region;
            config.ForcePathStyle = true;
            _https = endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            _server = Uri.TryCreate(endpoint, UriKind.Absolute, out var address) ? address.Authority : endpoint;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
            _https = true;
            _server = "s3." + region + ".amazonaws.com";
        }

        _client = new AmazonS3Client(new BasicAWSCredentials(account.User, account.Secret), config);
    }

    public override string Kind => DestinationKinds.S3;

    public override string Root { get; }

    public override string Label => "s3://" + _bucket + (Root.Length > 0 ? "/" + Root : string.Empty);

    public override string? Account => _account.User;

    public override string CacheKey => "s3://" + _server + "/" + _bucket;

    public override string? SeenFingerprint
    {
        get => _trust.Seen;
        protected set { }
    }

    public override bool RenamesFolders => false;

    public override bool HasFolders => false;

    /// <summary>
    /// Files are uploaded with the lock of the destination (<c>object_lock_days</c>). «Проверить доступ» turns it off
    /// for its trial file, which it wants to delete at once.
    /// </summary>
    public bool LockWrites { get; set; } = true;

    /// <summary>The bucket has Object Lock; null if the server does not tell.</summary>
    public bool? HasObjectLock
    {
        get
        {
            if (!_objectLockAsked)
            {
                _objectLockAsked = true;
                try
                {
                    var response = Call(token => _client.GetObjectLockConfigurationAsync(new GetObjectLockConfigurationRequest { BucketName = _bucket }, token), null);
                    var configuration = response.ObjectLockConfiguration;
                    _hasObjectLock = configuration?.ObjectLockEnabled == ObjectLockEnabled.Enabled;
                    if (configuration?.Rule?.DefaultRetention is { } retention)
                    {
                        _defaultRetention = retention.Days is > 0
                            ? Messages.Format(Messages.StoreLockDefaultDays, retention.Mode?.Value, retention.Days)
                            : retention.Years is > 0 ? Messages.Format(Messages.StoreLockDefaultYears, retention.Mode?.Value, retention.Years) : null;
                    }
                }
                catch (FileNotFoundException)
                {
                    _hasObjectLock = false;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // No right to ask, or a server that has no such question.
                    _hasObjectLock = ex is StoreException { InnerException: AmazonS3Exception { ErrorCode: "ObjectLockConfigurationNotFoundError" or "NoSuchObjectLockConfiguration" } } ? false : null;
                }
            }

            return _hasObjectLock;
        }
    }

    public override void CheckReady()
    {
        if (_bucket.Length == 0)
        {
            throw new StoreException(Messages.StoreBucketMissing);
        }

        if (_lockDays > 0 && HasObjectLock == false)
        {
            throw new StoreException(Messages.Format(Messages.StoreObjectLockMissing, _bucket, _lockDays));
        }
    }

    public override IReadOnlyList<string> Describe()
    {
        var lines = new List<string>();
        bool? locked = HasObjectLock;
        lines.Add(locked switch
        {
            true => _defaultRetention is null ? Messages.StoreLockOn : Messages.Format(Messages.StoreLockOnDefault, _defaultRetention),
            false => Messages.StoreLockOff,
            _ => Messages.StoreLockUnknown,
        });
        if (locked != true)
        {
            lines.Add(KeepsVersions ? Messages.StoreVersionsOn : Messages.StoreVersionsOff);
        }

        return lines;
    }

    public override void CreateFolder(string path)
    {
        // A folder is a part of the names of its files.
    }

    public override bool FolderExists(string path)
    {
        var response = Call(token => _client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket, Prefix = Prefix(path), MaxKeys = 1 }, token), path);
        return path.Length == 0 || response.S3Objects is { Count: > 0 };
    }

    public override bool FileExists(string path)
    {
        try
        {
            Head(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    public override List<StoreEntry> List(string folder)
    {
        var result = new List<StoreEntry>();
        string prefix = Prefix(folder);
        string? next = null;
        do
        {
            Cancellation.ThrowIfCancellationRequested();
            var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix, Delimiter = "/", ContinuationToken = next };
            var response = Call(token => _client.ListObjectsV2Async(request, token), folder);
            foreach (string common in response.CommonPrefixes ?? [])
            {
                string name = common[prefix.Length..].TrimEnd('/');
                if (name.Length > 0)
                {
                    result.Add(new StoreEntry(Combine(folder, name), name, true, 0, DateTime.MinValue));
                }
            }

            foreach (var item in response.S3Objects ?? [])
            {
                string name = item.Key[prefix.Length..];
                if (name.Length > 0 && !name.EndsWith('/'))
                {
                    result.Add(new StoreEntry(item.Key, name, false, item.Size ?? 0, Utc(item.LastModified)));
                }
            }

            next = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (next is not null);
        return result;
    }

    public override IEnumerable<StoreEntry> ListFiles(string folder, Func<string, bool>? skipFolder = null)
    {
        string prefix = Prefix(folder);
        var skipped = new Dictionary<string, bool>(StringComparer.Ordinal);
        string? next = null;
        do
        {
            Cancellation.ThrowIfCancellationRequested();
            var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix, ContinuationToken = next };
            var response = Call(token => _client.ListObjectsV2Async(request, token), folder);
            foreach (var item in response.S3Objects ?? [])
            {
                if (item.Key.EndsWith('/') || (skipFolder is not null && IsSkipped(item.Key, prefix.Length, skipFolder, skipped)))
                {
                    continue;
                }

                yield return new StoreEntry(item.Key, Name(item.Key), false, item.Size ?? 0, Utc(item.LastModified));
            }

            next = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (next is not null);
    }

    public override void DeleteFolder(string path) => DeleteAll(Prefix(path), exactKey: null);

    public override bool RemoveEmptyFolder(string path)
    {
        string prefix = Prefix(path);
        var response = Call(token => _client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix, MaxKeys = 2 }, token), path);
        var found = response.S3Objects ?? [];
        if (found.Any(item => item.Key != prefix))
        {
            return false;
        }

        if (found.Count > 0)
        {
            // Only the empty object some programs make to show a folder.
            DeleteAll(prefix, exactKey: prefix);
        }

        return true;
    }

    public override void MoveFolder(string source, string target) => throw new NotSupportedException();

    public override Stream OpenRead(string path)
    {
        long length = GetFileSize(path);
        return new RangeReadStream(length, offset =>
        {
            var request = new GetObjectRequest { BucketName = _bucket, Key = path };
            if (offset > 0)
            {
                request.ByteRange = new ByteRange(offset, length - 1);
            }

            var response = Call(token => _client.GetObjectAsync(request, token), path);
            return new GuardedStream(response.ResponseStream, ex => Translate(ex, path), response.Dispose);
        });
    }

    public override long GetFileSize(string path) => Head(path).ContentLength;

    public override StoreWriter BeginWrite(string path, string tempSuffix, StoreWriteMode mode = StoreWriteMode.Blocks)
    {
        if (mode == StoreWriteMode.Seekable)
        {
            throw new NotSupportedException();
        }

        Cancellation.ThrowIfCancellationRequested();
        return new Writer(this, path);
    }

    public override void DeleteFile(string path) => DeleteAll(path, exactKey: path);

    public override void MoveFile(string source, string target)
    {
        long length = GetFileSize(source);
        if (length <= CopyLimit)
        {
            var request = new CopyObjectRequest { SourceBucket = _bucket, SourceKey = source, DestinationBucket = _bucket, DestinationKey = target };
            if (Lock() is { } until)
            {
                request.ObjectLockMode = _lockMode;
                request.ObjectLockRetainUntilDate = until;
            }

            Call(token => _client.CopyObjectAsync(request, token), source, TransferTime(length));
        }
        else
        {
            CopyInParts(source, target, length);
        }

        DeleteFile(source);
    }

    public override DateTimeOffset? LockedUntil(string path)
    {
        if (HasObjectLock == false)
        {
            return null;
        }

        try
        {
            var response = Call(token => _client.GetObjectRetentionAsync(new GetObjectRetentionRequest { BucketName = _bucket, Key = path }, token), path);
            return response.Retention?.RetainUntilDate is { } until && until.ToUniversalTime() > DateTime.UtcNow
                ? new DateTimeOffset(until.ToUniversalTime())
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No lock on this object, no such object, or no right to ask: deleting will tell.
            return null;
        }
    }

    protected internal override Exception? Translate(Exception exception, string? path)
    {
        if (_trust.Failure(_server) is { } untrusted)
        {
            return untrusted;
        }

        switch (exception)
        {
            case DeleteObjectsException failed:
                // Some of many could not be deleted: the first one tells why.
                var first = failed.Response?.DeleteErrors?.FirstOrDefault();
                string reason = Messages.Format(Messages.StoreFailed, _server, first is null ? failed.Message : first.Key + ": " + first.Code + ": " + first.Message);
                return first?.Code == "AccessDenied" ? new UnauthorizedAccessException(reason, failed) : new StoreException(reason, failed);
            case AmazonS3Exception s3:
                string text = Messages.Format(Messages.StoreFailed, _server, (s3.ErrorCode is { Length: > 0 } code ? code + ": " : string.Empty) + s3.Message);
                return s3.ErrorCode switch
                {
                    "NoSuchBucket" => new StoreException(Messages.Format(Messages.StoreBucketNotFound, _server, _bucket), s3),
                    "InvalidAccessKeyId" or "SignatureDoesNotMatch" or "InvalidToken" =>
                        new StoreException(Messages.Format(Messages.StoreLoginRefused, _server, _account.User), s3),
                    _ => s3.StatusCode switch
                    {
                        HttpStatusCode.NotFound => new FileNotFoundException(text, path, s3),
                        HttpStatusCode.Forbidden => new UnauthorizedAccessException(text, s3),
                        _ => new StoreException(text, s3),
                    },
                };
            case AmazonClientException or HttpRequestException or AuthenticationException or TimeoutException:
                return new StoreException(Messages.Format(Messages.StoreUnreachable, _server, Deepest(exception).Message), exception);
            case AmazonServiceException or System.Xml.XmlException:
                // An answer the client does not understand: not S3 at the other end, or not quite.
                return new StoreException(Messages.Format(Messages.StoreFailed, _server, exception.Message), exception);
            case OperationCanceledException when !Cancellation.IsCancellationRequested:
                return new StoreException(Messages.Format(Messages.StoreFailed, _server, Messages.StoreNoAnswer), exception);
            default:
                return null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _client.Dispose();
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

    private static DateTime Utc(DateTime? time) => time?.ToUniversalTime() ?? DateTime.MinValue;

    /// <summary>The names of what lies in a folder start with this.</summary>
    private static string Prefix(string folder) => folder.Length == 0 || folder.EndsWith('/') ? folder : folder + "/";

    /// <summary>Whether a file lies in a folder the caller leaves out; every folder is asked about once.</summary>
    private static bool IsSkipped(string key, int from, Func<string, bool> skipFolder, Dictionary<string, bool> known)
    {
        for (int slash = key.IndexOf('/', from); slash >= 0; slash = key.IndexOf('/', slash + 1))
        {
            string folder = key[..slash];
            if (!known.TryGetValue(folder, out bool skip))
            {
                skip = skipFolder(folder);
                known[folder] = skip;
            }

            if (skip)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The bucket keeps (or kept) versions of its objects, so deleting has to name each of them.</summary>
    private bool KeepsVersions
    {
        get
        {
            if (_keepsVersions is null)
            {
                try
                {
                    var response = Call(token => _client.GetBucketVersioningAsync(new GetBucketVersioningRequest { BucketName = _bucket }, token), null);
                    _keepsVersions = response.VersioningConfig?.Status is { } status && (status == VersionStatus.Enabled || status == VersionStatus.Suspended);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A server without versions, or no right to ask.
                    _keepsVersions = false;
                }
            }

            return _keepsVersions.Value;
        }
    }

    /// <summary>Until when a file written now is locked; null without Object Lock.</summary>
    private DateTime? Lock() => LockWrites && _lockDays > 0 ? DateTime.UtcNow.AddDays(_lockDays) : null;

    private TimeSpan TransferTime(long bytes)
    {
        long rate = Limiter is null ? SlowestUpload : Math.Clamp(Limiter.BytesPerSecond / 2, 1024, SlowestUpload);
        return RequestTimeout + TimeSpan.FromSeconds(bytes / (double)rate);
    }

    private GetObjectMetadataResponse Head(string path)
    {
        try
        {
            return Call(token => _client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _bucket, Key = path }, token), path);
        }
        catch (UnauthorizedAccessException ex) when (ex.InnerException is AmazonS3Exception { ErrorCode: null or "" or "Forbidden" } && path.EndsWith('/'))
        {
            throw new FileNotFoundException(null, path);
        }
    }

    /// <summary>Runs a request with a time limit; failures come out as the engine knows them.</summary>
    private T Call<T>(Func<CancellationToken, Task<T>> request, string? path, TimeSpan? timeout = null)
    {
        Cancellation.ThrowIfCancellationRequested();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        limit.CancelAfter(timeout ?? RequestTimeout);
        try
        {
            return request(limit.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (Translate(ex, path) is { } failure)
        {
            throw failure;
        }
    }

    /// <summary>Deletes everything whose name starts with the prefix, or the one object named exactly so.</summary>
    private void DeleteAll(string prefix, string? exactKey)
    {
        if (exactKey is not null && !KeepsVersions)
        {
            Call(token => _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = exactKey }, token), exactKey);
            return;
        }

        var batch = new List<KeyVersion>();
        if (KeepsVersions)
        {
            string? keyMarker = null;
            string? versionMarker = null;
            do
            {
                Cancellation.ThrowIfCancellationRequested();
                var request = new ListVersionsRequest { BucketName = _bucket, Prefix = prefix, KeyMarker = keyMarker, VersionIdMarker = versionMarker };
                var response = Call(token => _client.ListVersionsAsync(request, token), prefix);
                foreach (var version in response.Versions ?? [])
                {
                    if (exactKey is null || version.Key == exactKey)
                    {
                        batch.Add(new KeyVersion { Key = version.Key, VersionId = version.VersionId });
                    }
                }

                Delete(batch, final: false);
                bool more = response.IsTruncated == true;
                keyMarker = more ? response.NextKeyMarker : null;
                versionMarker = more ? response.NextVersionIdMarker : null;
            }
            while (keyMarker is not null);
        }
        else
        {
            string? next = null;
            do
            {
                Cancellation.ThrowIfCancellationRequested();
                var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix, ContinuationToken = next };
                var response = Call(token => _client.ListObjectsV2Async(request, token), prefix);
                batch.AddRange((response.S3Objects ?? []).Select(item => new KeyVersion { Key = item.Key }));
                Delete(batch, final: false);
                next = response.IsTruncated == true ? response.NextContinuationToken : null;
            }
            while (next is not null);
        }

        Delete(batch, final: true);
    }

    /// <summary>Deletes the collected objects a thousand at a time; the rest waits for more unless this is the end.</summary>
    private void Delete(List<KeyVersion> batch, bool final)
    {
        while (batch.Count >= DeleteBatch || (final && batch.Count > 0))
        {
            var part = batch.GetRange(0, Math.Min(DeleteBatch, batch.Count));
            batch.RemoveRange(0, part.Count);
            if (!_singleDeletes && part.Count > 1)
            {
                try
                {
                    Call(token => _client.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = _bucket, Objects = part, Quiet = true }, token), part[0].Key);
                    continue;
                }
                catch (StoreException ex) when (ex.InnerException is AmazonS3Exception and not DeleteObjectsException { ErrorCode: "NotImplemented" or "MissingContentMD5" or "InvalidRequest" or "InvalidDigest" or "MethodNotAllowed" })
                {
                    // A server that cannot delete many at once: one by one from now on.
                    _singleDeletes = true;
                }
            }

            foreach (var item in part)
            {
                Cancellation.ThrowIfCancellationRequested();
                Call(token => _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = item.Key, VersionId = item.VersionId }, token), item.Key);
            }
        }
    }

    private void CopyInParts(string from, string to, long length)
    {
        var start = new InitiateMultipartUploadRequest { BucketName = _bucket, Key = to };
        if (Lock() is { } until)
        {
            start.ObjectLockMode = _lockMode;
            start.ObjectLockRetainUntilDate = until;
        }

        string uploadId = Call(token => _client.InitiateMultipartUploadAsync(start, token), to).UploadId;
        try
        {
            var parts = new List<PartETag>();
            for (long offset = 0; offset < length; offset += CopyPartSize)
            {
                var request = new CopyPartRequest
                {
                    SourceBucket = _bucket,
                    SourceKey = from,
                    DestinationBucket = _bucket,
                    DestinationKey = to,
                    UploadId = uploadId,
                    PartNumber = parts.Count + 1,
                    FirstByte = offset,
                    LastByte = Math.Min(length, offset + CopyPartSize) - 1,
                };
                var response = Call(token => _client.CopyPartAsync(request, token), from, TransferTime(CopyPartSize));
                parts.Add(new PartETag(parts.Count + 1, response.ETag));
            }

            Call(token => _client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest { BucketName = _bucket, Key = to, UploadId = uploadId, PartETags = parts }, token), to, StoreTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            AbortUpload(to, uploadId);
            throw;
        }
    }

    /// <summary>How long a server may take to put the parts of a large file together.</summary>
    private static TimeSpan StoreTime => TimeSpan.FromMinutes(15);

    private void AbortUpload(string key, string uploadId)
    {
        try
        {
            using var limit = new CancellationTokenSource(RequestTimeout);
            _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = _bucket, Key = key, UploadId = uploadId }, limit.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is AmazonClientException or AmazonServiceException or IOException or HttpRequestException or OperationCanceledException)
        {
            // The parts stay until the bucket's own rule removes unfinished uploads (see the README).
        }
    }

    /// <summary>
    /// A file being uploaded. It is collected in memory a part at a time; a file that fits into the first part goes up
    /// with one request. The parts grow with the file, since there can be ten thousand of them at most.
    /// </summary>
    private sealed class Writer : StoreWriter
    {
        private readonly S3Store _store;
        private readonly string _key;
        private readonly PartStream _stream;
        private readonly List<PartETag> _parts = [];
        private readonly DateTime? _lockedUntil;
        private string? _uploadId;
        private bool _closed;
        private bool _stored;

        public Writer(S3Store store, string key)
        {
            _store = store;
            _key = key;
            _lockedUntil = store.Lock();
            _stream = new PartStream(this);
        }

        public override Stream Stream => _stream;

        public override string ReadPath => _key;

        public override bool VisibleWhenClosed => true;

        public override void Close()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            var data = _stream.Take();
            if (_uploadId is null)
            {
                Put(data);
            }
            else
            {
                if (data.Count > 0)
                {
                    UploadPart(data);
                }

                _store.Call(
                    token => _store._client.CompleteMultipartUploadAsync(
                        new CompleteMultipartUploadRequest { BucketName = _store._bucket, Key = _key, UploadId = _uploadId, PartETags = _parts }, token),
                    _key,
                    StoreTime);
            }

            _stored = true;
            _stream.Release();
        }

        protected override void CommitCore()
        {
            // The object got its name when the upload was completed.
        }

        protected override void AbortCore()
        {
            _stream.Release();
            if (_stored)
            {
                _store.DeleteFile(_key);
            }
            else if (_uploadId is not null)
            {
                _store.AbortUpload(_key, _uploadId);
            }
        }

        /// <summary>The size of the next part: 16 MB for the first thousand, 64 MB up to five thousand, then 512 MB.</summary>
        private int PartSize => _parts.Count < 1000 ? FirstPartSize : _parts.Count < 5000 ? 4 * FirstPartSize : 32 * FirstPartSize;

        private void PartFull(ArraySegment<byte> data)
        {
            if (_uploadId is null)
            {
                var request = new InitiateMultipartUploadRequest { BucketName = _store._bucket, Key = _key };
                if (_lockedUntil is { } until)
                {
                    request.ObjectLockMode = _store._lockMode;
                    request.ObjectLockRetainUntilDate = until;
                }

                _uploadId = _store.Call(token => _store._client.InitiateMultipartUploadAsync(request, token), _key).UploadId;
            }

            UploadPart(data);
        }

        private void UploadPart(ArraySegment<byte> data)
        {
            int number = _parts.Count + 1;
            var response = _store.Call(
                token =>
                {
                    var request = new UploadPartRequest
                    {
                        BucketName = _store._bucket,
                        Key = _key,
                        UploadId = _uploadId,
                        PartNumber = number,
                        PartSize = data.Count,
                        InputStream = Body(data),
                        MD5Digest = ContentMd5(data),
                        DisablePayloadSigning = _store._https,
                        UseChunkEncoding = !_store._https,
                    };
                    return _store._client.UploadPartAsync(request, token);
                },
                _key,
                _store.TransferTime(data.Count));
            _parts.Add(new PartETag(number, response.ETag));
        }

        private void Put(ArraySegment<byte> data)
        {
            _store.Call(
                token =>
                {
                    var request = new PutObjectRequest
                    {
                        BucketName = _store._bucket,
                        Key = _key,
                        InputStream = Body(data),
                        AutoCloseStream = true,
                        MD5Digest = ContentMd5(data),
                        DisablePayloadSigning = _store._https,
                        UseChunkEncoding = !_store._https,
                    };
                    if (_lockedUntil is { } until)
                    {
                        request.ObjectLockMode = _store._lockMode;
                        request.ObjectLockRetainUntilDate = until;
                    }

                    return _store._client.PutObjectAsync(request, token);
                },
                _key,
                _store.TransferTime(data.Count));
        }

        /// <summary>The Content-MD5 of a request: S3 demands it for uploads with Object Lock, and it guards the way there.</summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "The S3 protocol asks for MD5 as a transport checksum; nothing is secured by it.")]
        private static string ContentMd5(ArraySegment<byte> data) => Convert.ToBase64String(MD5.HashData(data));

        /// <summary>What is sent: the part from memory, read at the speed the destination allows.</summary>
        private Stream Body(ArraySegment<byte> data)
        {
            var memory = new MemoryStream(data.Array!, data.Offset, data.Count, writable: false);
            return _store.Limiter is null ? memory : new ThrottledStream(memory, _store.Limiter, _store.Cancellation);
        }

        /// <summary>Collects what the engine writes and hands over every full part.</summary>
        private sealed class PartStream(Writer writer) : Stream
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

            /// <summary>What is left for the last part (or for the whole file, if it is small).</summary>
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
                    int size = writer.PartSize;
                    if (_count >= size)
                    {
                        writer.PartFull(new ArraySegment<byte>(_buffer, 0, _count));
                        _count = 0;
                        size = writer.PartSize;
                    }

                    int copied = Math.Min(buffer.Length, size - _count);
                    if (_buffer.Length < _count + copied)
                    {
                        // Small files stay small: the buffer grows as the file does, up to the size of a part.
                        Array.Resize(ref _buffer, (int)Math.Min(size, Math.Max((long)_count + copied, Math.Max(64 * 1024, _buffer.Length * 2L))));
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

    /// <summary>
    /// The HTTP connection of the client: one for the life of the store, with no time limit of its own (every request
    /// has one) and with the certificate check of the destination.
    /// </summary>
    private sealed class PinnedHttp(ServerTrust trust) : HttpClientFactory, IDisposable
    {
        private readonly Lock _sync = new();
        private HttpClient? _client;

        public override HttpClient CreateHttpClient(IClientConfig clientConfig)
        {
            lock (_sync)
            {
                if (_client is null)
                {
                    var handler = new SocketsHttpHandler
                    {
                        ConnectTimeout = ConnectTimeout,
                        AutomaticDecompression = DecompressionMethods.None,
                        UseCookies = false,
                        AllowAutoRedirect = false,
                    };
                    handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) => trust.Validate(certificate, errors);
                    _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                }

                return _client;
            }
        }

        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;

        public void Dispose()
        {
            lock (_sync)
            {
                _client?.Dispose();
                _client = null;
            }
        }
    }

}
