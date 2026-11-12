using System.Diagnostics;
using Amazon.S3.Model;
using Custos.Core.Backup;
using Custos.Core.Destinations;
using Custos.Core.Model;

namespace Custos.Core.Tests;

/// <summary>
/// What the backup engine relies on in every kind of destination (section 6.3): the same checks run against a local
/// folder and against real SFTP, FTP, FTPS, WebDAV and S3 servers (see <see cref="TestDestinations"/>).
/// </summary>
public sealed class DestinationStoreTests : IDisposable
{
    private const string Suffix = BackupEngine.TempSuffix;

    private readonly TempFolder _temp = new();
    private readonly TestPlatform _platform = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [MemberData(nameof(TestDestinations.All), MemberType = typeof(TestDestinations))]
    public void A_file_appears_under_its_name_only_when_committed(string kind)
    {
        using var store = TestDestinations.Open(kind, _platform, _temp);
        string folder = store.Combine(store.Root, "Документы", "2026 год");
        string path = store.Combine(folder, "Отчёт #1 (100%) & итог+.bin");
        byte[] data = Bytes(3 * 1024 * 1024 + 17, seed: 1);
        store.CreateFolder(folder);
        store.CreateFolder(folder);

        using (var writer = store.BeginWrite(path, Suffix))
        {
            Write(writer.Stream, data, 700_000);
            if (!writer.VisibleWhenClosed)
            {
                Assert.False(store.FileExists(path));
            }

            writer.Close();
            Assert.Equal(data, ReadAll(store, writer.ReadPath));
            Assert.Equal(writer.VisibleWhenClosed, store.FileExists(path));
            writer.Commit();
        }

        Assert.True(store.FileExists(path));
        Assert.False(store.FileExists(path + Suffix));
        Assert.Equal(data.Length, store.GetFileSize(path));
        Assert.Equal(data, ReadAll(store, path));

        var listed = Assert.Single(store.List(folder));
        Assert.Equal(path, listed.Path);
        Assert.Equal("Отчёт #1 (100%) & итог+.bin", listed.Name);
        Assert.False(listed.IsFolder);
        Assert.Equal(data.Length, listed.Size);
        var above = Assert.Single(store.List(store.Combine(store.Root, "Документы")));
        Assert.True(above.IsFolder);
        Assert.Equal("2026 год", above.Name);
        Assert.Equal(folder, above.Path);
        Assert.Equal([path], store.ListFiles(store.Root).Select(e => e.Path));
        Assert.Equal(Path.Combine("Документы", "2026 год", listed.Name).Replace('\\', '/'), store.Relative(store.Root, path).Replace('\\', '/'));
        Assert.Equal(folder, store.Parent(path));
        Assert.Equal(listed.Name, store.Name(path));
    }

    [Theory]
    [MemberData(nameof(TestDestinations.All), MemberType = typeof(TestDestinations))]
    public void A_file_is_read_from_any_position(string kind)
    {
        using var store = TestDestinations.Open(kind, _platform, _temp);
        string path = store.Combine(store.Root, "data.bin");
        byte[] data = Bytes(1_500_000, seed: 2);
        Put(store, path, data);

        using var stream = store.OpenRead(path);
        Assert.True(stream.CanSeek);
        Assert.Equal(data.Length, stream.Length);

        // The way a ZIP reader goes: the end first, then here and there, then forward.
        foreach (var (offset, count) in new[] { (data.Length - 22, 22), (data.Length - 70_000, 3000), (10, 5), (400_000, 300_000), (700_000, 1), (0, 100), (1_499_999, 1) })
        {
            stream.Seek(offset, SeekOrigin.Begin);
            byte[] part = new byte[count];
            stream.ReadExactly(part);
            Assert.Equal(data.AsSpan(offset, count).ToArray(), part);
        }

        stream.Seek(-5, SeekOrigin.End);
        Assert.Equal(5, stream.Read(new byte[100], 0, 100));
        Assert.Equal(0, stream.Read(new byte[100], 0, 100));
    }

    [Theory]
    [MemberData(nameof(TestDestinations.All), MemberType = typeof(TestDestinations))]
    public void An_abandoned_file_leaves_nothing(string kind)
    {
        using var store = TestDestinations.Open(kind, _platform, _temp);
        string path = store.Combine(store.Root, "half.bin");
        var writer = store.BeginWrite(path, Suffix);
        Write(writer.Stream, Bytes(2_000_000, seed: 3), 500_000);
        writer.Abort();
        writer.Dispose();

        using var written = store.BeginWrite(path, Suffix);
        Write(written.Stream, Bytes(1000, seed: 4), 1000);
        written.Close();
        written.Abort();

        Assert.Empty(store.List(store.Root));
        Assert.False(store.FileExists(path));
    }

    [Theory]
    [MemberData(nameof(TestDestinations.All), MemberType = typeof(TestDestinations))]
    public void Files_are_replaced_moved_and_deleted(string kind)
    {
        using var store = TestDestinations.Open(kind, _platform, _temp);
        string first = store.Combine(store.Root, "first.txt");
        string second = store.Combine(store.Root, "sub", "second.txt");
        Put(store, first, [1, 2, 3]);
        Put(store, first, [4, 5]);
        Assert.Equal(new byte[] { 4, 5 }, ReadAll(store, first));

        store.CreateFolder(store.Combine(store.Root, "sub"));
        Put(store, second, [9]);
        store.MoveFile(first, second);
        Assert.False(store.FileExists(first));
        Assert.Equal(new byte[] { 4, 5 }, ReadAll(store, second));

        Put(store, first, []);
        Assert.Equal(0, store.GetFileSize(first));
        Assert.Empty(ReadAll(store, first));

        store.DeleteFile(first);
        store.DeleteFile(first);
        Assert.False(store.FileExists(first));
        Assert.Throws<FileNotFoundException>(() => store.OpenRead(first).Dispose());
        Assert.Throws<FileNotFoundException>(() => store.GetFileSize(first));
        Assert.False(store.FileExists(store.Combine(store.Root, "sub")));
    }

    [Theory]
    [MemberData(nameof(TestDestinations.All), MemberType = typeof(TestDestinations))]
    public void Folders_are_listed_emptied_and_removed(string kind)
    {
        using var store = TestDestinations.Open(kind, _platform, _temp);
        string set = store.Combine(store.Root, "Set 1");
        string deep = store.Combine(set, "a", "b");
        store.CreateFolder(deep);
        Put(store, store.Combine(deep, "one.txt"), [1]);
        Put(store, store.Combine(set, "two.txt"), [2, 2]);
        Put(store, store.Combine(set, "a", "three.txt"), [3, 3, 3]);

        Assert.True(store.FolderExists(set));
        Assert.False(store.FolderExists(store.Combine(store.Root, "No such")));
        Assert.Empty(store.List(store.Combine(store.Root, "No such")));
        Assert.Equal(["a", "two.txt"], store.List(set).Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal(6, store.ListFiles(set).Sum(e => e.Size));
        Assert.Equal(["two.txt"], store.ListFiles(set, folder => store.Name(folder) == "a").Select(e => e.Name));
        Assert.False(store.RemoveEmptyFolder(store.Combine(set, "a")));

        if (store.RenamesFolders)
        {
            string renamed = store.Combine(store.Root, "Set 2");
            store.MoveFolder(set, renamed);
            Assert.False(store.FolderExists(set));
            Assert.Equal(3, store.ListFiles(renamed).Count());
            set = renamed;
        }

        store.DeleteFile(store.Combine(store.Combine(set, "a", "b"), "one.txt"));
        Assert.True(store.RemoveEmptyFolder(store.Combine(set, "a", "b")));
        Assert.True(store.RemoveEmptyFolder(store.Combine(set, "a", "b")));
        Assert.Equal(["three.txt"], store.List(store.Combine(set, "a")).Select(e => e.Name));

        store.DeleteFolder(set);
        store.DeleteFolder(set);
        Assert.False(store.FolderExists(set));
        Assert.Empty(store.List(store.Root));
    }

    [Theory]
    [MemberData(nameof(TestDestinations.All), MemberType = typeof(TestDestinations))]
    public void A_large_file_arrives_whole(string kind)
    {
        using var store = TestDestinations.Open(kind, _platform, _temp);
        string path = store.Combine(store.Root, "large.bin");

        // More than two parts of an S3 upload.
        byte[] data = Bytes(37 * 1024 * 1024 + 5, seed: 5);
        using (var writer = store.BeginWrite(path, Suffix, StoreWriteMode.Stream))
        {
            Write(writer.Stream, data, 81_920);
            writer.Commit();
        }

        Assert.Equal(data.Length, store.GetFileSize(path));
        using var stream = store.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(data), sha.ComputeHash(stream));
    }

    [Theory]
    [MemberData(nameof(TestDestinations.All), MemberType = typeof(TestDestinations))]
    public void A_seekable_file_is_changed_after_it_was_written(string kind)
    {
        using var store = TestDestinations.Open(kind, _platform, _temp);
        Assert.SkipUnless(store.CanSeek, "This kind writes strictly forward.");
        string path = store.Combine(store.Root, "archive.7z.001");
        byte[] data = Bytes(2_000_000, seed: 6);
        byte[] expected = [.. data];
        using var writer = store.BeginWrite(path, Suffix, StoreWriteMode.Seekable);
        var stream = writer.Stream;

        // The way 7z writes: a placeholder, the data, then the real header over the placeholder.
        stream.Write(new byte[32]);
        stream.Write(data.AsSpan(32));
        Assert.Equal(data.Length, stream.Position);
        stream.Seek(0, SeekOrigin.Begin);
        stream.Write(data.AsSpan(0, 32));
        stream.Seek(1_000_000, SeekOrigin.Begin);
        stream.Write([0xAA, 0xBB]);
        expected[1_000_000] = 0xAA;
        expected[1_000_001] = 0xBB;
        writer.Close();
        Assert.Equal(expected, ReadAll(store, writer.ReadPath));

        // And the first volume once more when the last one is complete.
        using (var again = writer.Reopen())
        {
            again.Seek(12, SeekOrigin.Begin);
            again.Write([1, 2, 3, 4]);
        }

        new byte[] { 1, 2, 3, 4 }.CopyTo(expected, 12);
        writer.Commit();
        Assert.Equal(expected, ReadAll(store, path));
    }

    [Theory]
    [MemberData(nameof(TestDestinations.Remote), MemberType = typeof(TestDestinations))]
    public void A_wrong_password_is_told_apart_from_a_broken_server(string kind)
    {
        Assert.SkipWhen(kind == TestDestinations.SftpKey, "The key is the password here.");
        var destination = TestDestinations.Create(kind, _platform, _temp);
        Assert.SkipWhen(kind == TestDestinations.S3 && TestDestinations.IsMoto, "moto lets anyone in.");
        var account = _platform.StoreAccounts[destination.CredentialId!];
        _platform.StoreAccounts[destination.CredentialId!] = account with { Secret = "not-the-password" };
        using var store = _platform.OpenStore(destination);
        var failure = Assert.ThrowsAny<Exception>(() => store.List(store.Root));
        Assert.True(failure is IOException or UnauthorizedAccessException, failure.ToString());
        Assert.IsNotType<ServerTrustException>(failure);
        if (kind is not (TestDestinations.S3 or TestDestinations.WebDav or TestDestinations.WebDavs) || failure is StoreException)
        {
            Assert.Contains(account.User, failure.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(TestDestinations.Remote), MemberType = typeof(TestDestinations))]
    public void A_server_that_is_not_there_is_a_failure_of_the_destination(string kind)
    {
        var destination = TestDestinations.Create(kind, _platform, _temp);
        if (destination.Kind == DestinationKinds.S3)
        {
            destination.Endpoint = "http://127.0.0.1:1";
        }
        else if (destination.Kind == DestinationKinds.WebDav)
        {
            destination.Host = "http://127.0.0.1:1/dav";
        }
        else
        {
            destination.Port = 1;
        }

        using var store = _platform.OpenStore(destination);
        Assert.ThrowsAny<IOException>(() => store.FolderExists(store.Root));
    }

    [Theory]
    [MemberData(nameof(TestDestinations.Pinned), MemberType = typeof(TestDestinations))]
    public void An_unknown_or_changed_server_gets_nothing(string kind)
    {
        var destination = TestDestinations.Create(kind, _platform, _temp);
        string trusted = destination.ServerFingerprint!;
        Assert.NotEmpty(trusted);
        using (var store = _platform.OpenStore(destination))
        {
            store.CreateFolder(store.Root);
            Assert.Equal(trusted, ((RemoteStore)store).SeenFingerprint);
        }

        destination.ServerFingerprint = null;
        using (var store = _platform.OpenStore(destination))
        {
            var unknown = Assert.Throws<ServerTrustException>(() => store.BeginWrite(store.Combine(store.Root, "x"), Suffix).Commit());
            Assert.Null(unknown.Expected);
            Assert.Equal(trusted, unknown.Seen);
            Assert.Contains(trusted, unknown.Message, StringComparison.Ordinal);
        }

        string other = kind == TestDestinations.Sftp ? "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" : string.Join(':', Enumerable.Repeat("AB", 32));
        destination.ServerFingerprint = other;
        using (var store = _platform.OpenStore(destination))
        {
            var changed = Assert.Throws<ServerTrustException>(() => store.List(store.Root));
            Assert.Equal(other, changed.Expected);
            Assert.Equal(trusted, changed.Seen);
        }
    }

    [Fact]
    public async Task An_s3_bucket_with_object_lock_refuses_to_delete_before_the_time()
    {
        var cancellation = TestContext.Current.CancellationToken;
        Assert.SkipWhen(TestDestinations.IsMoto, "moto knows no Object Lock to speak of; MinIO does (the CI job).");
        var destination = TestDestinations.Create(TestDestinations.S3Lock, _platform, _temp);
        using var store = (S3Store)_platform.OpenStore(destination);
        store.CheckReady();
        Assert.True(store.HasObjectLock);
        string path = store.Combine(store.Root, "Set", "locked.bin");
        Put(store, path, Bytes(100_000, seed: 7));
        string large = store.Combine(store.Root, "Set", "large.bin");
        Put(store, large, Bytes(17 * 1024 * 1024, seed: 8));

        foreach (string locked in new[] { path, large })
        {
            var until = store.LockedUntil(locked);
            Assert.NotNull(until);
            Assert.InRange(until.Value, DateTimeOffset.UtcNow.AddHours(23), DateTimeOffset.UtcNow.AddHours(25));
            Assert.Throws<UnauthorizedAccessException>(() => store.DeleteFile(locked));
            Assert.True(store.FileExists(locked));
        }

        Assert.Throws<UnauthorizedAccessException>(() => store.DeleteFolder(store.Combine(store.Root, "Set")));
        Assert.Equal(2, store.ListFiles(store.Root).Count());

        // The trial file of «Проверить доступ» is not locked.
        store.LockWrites = false;
        string probe = store.Combine(store.Root, "probe.tmp");
        Put(store, probe, [1]);
        Assert.Null(store.LockedUntil(probe));
        store.DeleteFile(probe);
        Assert.False(store.FileExists(probe));

        // When the time is over (here: cut short by the owner of the bucket), every version goes.
        using var client = TestDestinations.S3Client();
        foreach (string locked in new[] { path, large })
        {
            await client.PutObjectRetentionAsync(
                new PutObjectRetentionRequest
                {
                    BucketName = destination.Bucket,
                    Key = locked,
                    BypassGovernanceRetention = true,
                    Retention = new ObjectLockRetention { Mode = Amazon.S3.ObjectLockRetentionMode.Governance, RetainUntilDate = DateTime.UtcNow.AddSeconds(2) },
                },
                cancellation);
        }

        await Task.Delay(TimeSpan.FromSeconds(3), cancellation);
        Assert.Null(store.LockedUntil(path));
        store.DeleteFolder(store.Combine(store.Root, "Set"));
        var versions = await client.ListVersionsAsync(new ListVersionsRequest { BucketName = destination.Bucket, Prefix = store.Root + "/" }, cancellation);
        Assert.Empty(versions.Versions ?? []);
    }

    [Fact]
    public void An_s3_bucket_without_object_lock_cannot_promise_one()
    {
        var destination = TestDestinations.Create(TestDestinations.S3, _platform, _temp);
        destination.ObjectLockDays = 30;
        using var store = (S3Store)_platform.OpenStore(destination);
        Assert.False(store.HasObjectLock);
        var failure = Assert.Throws<StoreException>(store.CheckReady);
        Assert.Contains(destination.Bucket!, failure.Message, StringComparison.Ordinal);
        Assert.Contains(store.Describe(), line => line == Messages.StoreLockOff);
    }

    [Fact]
    public void A_speed_limit_holds_the_writer_back()
    {
        var limiter = SpeedLimiter.ForKbps(256)!;
        using var target = new MemoryStream();
        using var stream = new ThrottledStream(target, limiter);
        var clock = Stopwatch.StartNew();
        stream.Write(new byte[512 * 1024]);
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(1.4), TimeSpan.FromSeconds(10));
        Assert.Equal(512 * 1024, target.Length);
        Assert.Null(SpeedLimiter.ForKbps(0));
    }

    [Fact]
    public void A_patched_upload_keeps_changes_to_what_was_sent()
    {
        using var sent = new MemoryStream();
        var stream = new PatchedWriteStream(sent);
        stream.Write([1, 2, 3, 4, 5]);
        stream.Seek(1, SeekOrigin.Begin);
        stream.Write([9, 9]);
        Assert.Equal(3, stream.Position);
        stream.Seek(4, SeekOrigin.Begin);

        // Over the end: one byte changes what was sent, two go on.
        stream.Write([7, 8, 8]);
        stream.Seek(9, SeekOrigin.Begin);
        stream.Write([6]);
        stream.SetLength(12);
        stream.Finish();

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 8, 8, 0, 0, 6, 0, 0 }, sent.ToArray());
        Assert.Equal(["1:0909", "4:07"], stream.Patches.Select(p => p.Offset + ":" + Convert.ToHexString(p.Data)));
        Assert.Null(stream.CutTo);
        stream.SetLength(4);
        Assert.Equal(4, stream.CutTo);
    }

    private static byte[] Bytes(int count, int seed)
    {
        byte[] data = new byte[count];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static void Write(Stream stream, byte[] data, int piece)
    {
        for (int offset = 0; offset < data.Length; offset += piece)
        {
            stream.Write(data, offset, Math.Min(piece, data.Length - offset));
        }
    }

    private static void Put(DestinationStore store, string path, byte[] data)
    {
        using var writer = store.BeginWrite(path, Suffix);
        Write(writer.Stream, data, 256 * 1024);
        writer.Commit();
    }

    private static byte[] ReadAll(DestinationStore store, string path)
    {
        using var stream = store.OpenRead(path);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
