using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Custos.Core.Destinations;
using Custos.Core.Model;

namespace Custos.Core.Tests;

/// <summary>
/// The destinations the store tests run against. "local" is a temporary folder and always there; the others are real
/// servers named by variables that client/tools/ci-destinations.sh writes:
/// <c>CUSTOS_TEST_SFTP</c> = host|port|user|password|folder|private key file,
/// <c>CUSTOS_TEST_FTP</c> and <c>CUSTOS_TEST_FTPS</c> = host|port|user|password|folder,
/// <c>CUSTOS_TEST_WEBDAV</c> and <c>CUSTOS_TEST_WEBDAVS</c> = address|user|password,
/// <c>CUSTOS_TEST_S3</c> = endpoint|access key|secret key|server ("minio" or "moto").
/// Where a variable is missing the test is skipped, unless <c>CUSTOS_REQUIRE_DESTINATIONS=1</c> (the CI job).
/// </summary>
internal static class TestDestinations
{
    public const string Local = "local";
    public const string Sftp = "sftp";
    public const string SftpKey = "sftp-key";
    public const string Ftp = "ftp";
    public const string Ftps = "ftps";
    public const string WebDav = "webdav";
    public const string WebDavs = "webdavs";
    public const string S3 = "s3";

    /// <summary>S3 with Object Lock: a bucket of its own, every file locked for a day.</summary>
    public const string S3Lock = "s3-lock";

    public const string PlainBucket = "custos-test";
    public const string LockBucket = "custos-test-lock";

    private static readonly Lock Sync = new();
    private static readonly Dictionary<string, string> Fingerprints = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Buckets = new(StringComparer.Ordinal);

    /// <summary>Every kind, for theories.</summary>
    public static TheoryData<string> All => [Local, Sftp, SftpKey, Ftp, Ftps, WebDav, WebDavs, S3];

    /// <summary>The kinds on servers.</summary>
    public static TheoryData<string> Remote => [Sftp, SftpKey, Ftp, Ftps, WebDav, WebDavs, S3];

    /// <summary>The servers that show a key or a self-signed certificate.</summary>
    public static TheoryData<string> Pinned => [Sftp, Ftps, WebDavs];

    /// <summary>The S3 server is moto, a stand-in for development that checks no passwords and has no Object Lock.</summary>
    public static bool IsMoto => Settings(S3) is { Length: > 3 } settings && settings[3] == "moto";

    /// <summary>The variable of a kind, split at "|"; skips the test when it is not set.</summary>
    public static string[] Settings(string kind)
    {
        string name = "CUSTOS_TEST_" + kind switch
        {
            SftpKey => "SFTP",
            S3Lock => "S3",
            _ => kind.ToUpperInvariant(),
        };
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value) && Environment.GetEnvironmentVariable("CUSTOS_REQUIRE_DESTINATIONS") == "1")
        {
            Assert.Fail($"{name} is required here (CUSTOS_REQUIRE_DESTINATIONS=1).");
        }

        Assert.SkipWhen(string.IsNullOrEmpty(value), $"No server: {name} is not set.");
        return value!.Split('|');
    }

    /// <summary>
    /// A destination of the kind whose folder is new and empty, with its account in <paramref name="platform"/>.
    /// The key or certificate of the server is confirmed the way «Проверить доступ» does it.
    /// </summary>
    public static TaskDestination Create(string kind, TestPlatform platform, TempFolder temp, bool trusted = true)
    {
        string folder = "t" + Guid.NewGuid().ToString("N")[..12];
        if (kind == Local)
        {
            return new TaskDestination { Kind = DestinationKinds.Local, Path = temp.Combine(folder) };
        }

        string[] settings = Settings(kind);
        var destination = new TaskDestination { CredentialId = Guid.NewGuid().ToString() };
        switch (kind)
        {
            case Sftp or SftpKey:
                destination.Kind = DestinationKinds.Sftp;
                destination.Host = settings[0];
                destination.Port = int.Parse(settings[1], System.Globalization.CultureInfo.InvariantCulture);
                destination.Path = settings[4] + "/" + folder;
                platform.StoreAccounts[destination.CredentialId] = new StoreAccount(settings[2], kind == SftpKey ? File.ReadAllText(settings[5]) : settings[3]);
                break;
            case Ftp or Ftps:
                destination.Kind = kind == Ftp ? DestinationKinds.Ftp : DestinationKinds.Ftps;
                destination.Host = settings[0];
                destination.Port = int.Parse(settings[1], System.Globalization.CultureInfo.InvariantCulture);
                destination.Path = settings[4] + "/" + folder;
                platform.StoreAccounts[destination.CredentialId] = new StoreAccount(settings[2], settings[3]);
                break;
            case WebDav or WebDavs:
                destination.Kind = DestinationKinds.WebDav;
                destination.Host = settings[0];
                destination.Path = folder;
                platform.StoreAccounts[destination.CredentialId] = new StoreAccount(settings[1], settings[2]);
                break;
            default:
                destination.Kind = DestinationKinds.S3;
                destination.Endpoint = settings[0];
                destination.Bucket = kind == S3Lock ? LockBucket : PlainBucket;
                destination.Path = "tests/" + folder;
                destination.ObjectLockDays = kind == S3Lock ? 1 : 0;
                platform.StoreAccounts[destination.CredentialId] = new StoreAccount(settings[1], settings[2]);
                EnsureBucket(settings, destination.Bucket, kind == S3Lock);
                break;
        }

        if (trusted && kind is Sftp or SftpKey or Ftps or WebDavs)
        {
            destination.ServerFingerprint = Fingerprint(kind, destination, platform);
        }

        return destination;
    }

    /// <summary>A store on a new, empty folder of the kind.</summary>
    public static DestinationStore Open(string kind, TestPlatform platform, TempFolder temp)
    {
        var destination = Create(kind, platform, temp);
        var store = platform.OpenStore(destination);
        store.CreateFolder(store.Root);
        return store;
    }

    /// <summary>The client of the S3 server itself, for what the tests set up and look at behind the store's back.</summary>
    public static AmazonS3Client S3Client()
    {
        string[] settings = Settings(S3);
        return new AmazonS3Client(new BasicAWSCredentials(settings[1], settings[2]), new AmazonS3Config
        {
            ServiceURL = settings[0],
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });
    }

    /// <summary>What the server shows to someone who does not know it yet.</summary>
    private static string Fingerprint(string kind, TaskDestination destination, TestPlatform platform)
    {
        string server = kind == SftpKey ? Sftp : kind;
        lock (Sync)
        {
            if (!Fingerprints.TryGetValue(server, out string? seen))
            {
                using var store = platform.OpenStore(destination);
                var refused = Assert.Throws<ServerTrustException>(() => store.FolderExists(store.Root));
                Assert.Null(refused.Expected);
                seen = refused.Seen;
                Assert.NotEmpty(seen);
                Fingerprints.Add(server, seen);
            }

            return seen;
        }
    }

    private static void EnsureBucket(string[] settings, string bucket, bool objectLock)
    {
        lock (Sync)
        {
            if (!Buckets.Add(bucket))
            {
                return;
            }

            using var client = S3Client();
            try
            {
                client.PutBucketAsync(new PutBucketRequest { BucketName = bucket, ObjectLockEnabledForBucket = objectLock }).GetAwaiter().GetResult();
            }
            catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
            {
                // Left by an earlier run of the tests.
            }
        }
    }
}
