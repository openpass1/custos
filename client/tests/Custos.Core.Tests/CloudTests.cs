using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Custos.Core.Agent;
using Custos.Core.Backup;
using Custos.Core.Cloud;
using Custos.Core.Destinations;
using Custos.Core.Model;

namespace Custos.Core.Tests;

/// <summary>
/// Cloud drives (client stage 4b, section 6.3): uploads that survive broken connections, tokens that run out or are
/// revoked, archives in parts, the sign-in with PKCE, and the accounts that only this computer can connect.
/// </summary>
public sealed class CloudTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Modified = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private readonly TempFolder _temp = new();
    private readonly TestPlatform _platform = new();
    private readonly MemoryFileStateStore _state = new();
    private readonly TestLog _log = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData(TestDestinations.GDrive)]
    [InlineData(TestDestinations.Dropbox)]
    public void A_broken_chunk_is_sent_again_from_where_the_drive_stopped(string kind)
    {
        var destination = TestDestinations.Create(kind, _platform, _temp);
        var cloud = _platform.Clouds[destination.CredentialId!];
        using var store = _platform.OpenStore(destination);
        store.CreateFolder(store.Root);
        string path = store.Combine(store.Root, "big.zip");
        byte[] data = RandomBytes(2 * CloudStore.ChunkSize + 3 * 1024 * 1024 + 7);

        // The connection breaks twice in the middle of a chunk, and the answer to a whole chunk is lost once.
        cloud.Breaks.Enqueue(5 * 1024 * 1024 + 3);
        cloud.Breaks.Enqueue(123_457);
        cloud.LoseAnswers = 1;
        using (var writer = store.BeginWrite(path, BackupEngine.TempSuffix, StoreWriteMode.Stream))
        {
            writer.Stream.Write(data);
            writer.Close();
            writer.Commit();
        }

        Assert.Empty(cloud.Breaks);
        Assert.Equal(0, cloud.LoseAnswers);
        Assert.Equal(data.Length, store.GetFileSize(path));
        Assert.Equal(SHA256.HashData(data), Hash(store, path));
        Assert.Equal(1, kind == TestDestinations.GDrive ? ((FakeGoogleDrive)cloud).Sessions : ((FakeDropbox)cloud).Sessions);
    }

    [Theory]
    [InlineData(TestDestinations.GDrive)]
    [InlineData(TestDestinations.Dropbox)]
    public void The_lost_answer_to_the_last_chunk_still_finishes_the_file(string kind)
    {
        var destination = TestDestinations.Create(kind, _platform, _temp);
        var cloud = _platform.Clouds[destination.CredentialId!];
        using var store = _platform.OpenStore(destination);
        store.CreateFolder(store.Root);
        string path = store.Combine(store.Root, "two chunks.zip");
        byte[] data = RandomBytes(CloudStore.ChunkSize + 1000);
        using (var writer = store.BeginWrite(path, BackupEngine.TempSuffix, StoreWriteMode.Stream))
        {
            writer.Stream.Write(data);

            // Only the last chunk is left: its answer is lost after the file was made.
            cloud.LoseAnswers = 1;
            writer.Close();
            writer.Commit();
        }

        Assert.Equal(0, cloud.LoseAnswers);
        Assert.Equal(SHA256.HashData(data), Hash(store, path));
        Assert.Single(store.List(store.Root));
    }

    [Fact]
    public void A_broken_upload_to_yandex_disk_fails_and_leaves_nothing()
    {
        // Yandex Disk has no documented way to go on with an upload (section 6.3): the file fails, the destination
        // with it, and the next run starts it again.
        var destination = TestDestinations.Create(TestDestinations.YaDisk, _platform, _temp);
        var cloud = (FakeYandexDisk)_platform.Clouds[destination.CredentialId!];
        using var store = _platform.OpenStore(destination);
        store.CreateFolder(store.Root);
        string path = store.Combine(store.Root, "broken.zip");
        cloud.Breaks.Enqueue(300_000);
        var writer = store.BeginWrite(path, BackupEngine.TempSuffix, StoreWriteMode.Stream);
        var failure = Record.Exception(() =>
        {
            writer.Stream.Write(RandomBytes(1_000_000));
            writer.Close();
        });
        writer.Abort();
        writer.Dispose();

        var io = Assert.IsAssignableFrom<IOException>(failure);
        Assert.Contains(Messages.CloudYaDisk, io.Message, StringComparison.Ordinal);
        Assert.False(cloud.HasFile(path));
        Assert.Empty(store.List(store.Root));
    }

    [Fact]
    public void A_file_larger_than_yandex_disk_takes_is_named_as_such()
    {
        var destination = TestDestinations.Create(TestDestinations.YaDisk, _platform, _temp);
        var cloud = (FakeYandexDisk)_platform.Clouds[destination.CredentialId!];
        cloud.LargestFile = 100_000;
        using var store = _platform.OpenStore(destination);
        store.CreateFolder(store.Root);
        using var writer = store.BeginWrite(store.Combine(store.Root, "large.zip"), BackupEngine.TempSuffix, StoreWriteMode.Stream);
        writer.Stream.Write(RandomBytes(200_000));
        var failure = Assert.ThrowsAny<StoreException>(writer.Close);
        Assert.Equal(Messages.Format(Messages.CloudFileTooLarge, Messages.CloudYaDisk, store.Combine(store.Root, "large.zip")), failure.Message);
    }

    [Theory]
    [MemberData(nameof(TestDestinations.Clouds), MemberType = typeof(TestDestinations))]
    public void A_busy_or_failing_drive_is_waited_for(string kind)
    {
        var destination = TestDestinations.Create(kind, _platform, _temp);
        var cloud = _platform.Clouds[destination.CredentialId!];
        using var store = _platform.OpenStore(destination);
        store.CreateFolder(store.Root);
        int troubles = 0;
        cloud.Trouble = _ =>
        {
            troubles++;
            switch (troubles)
            {
                case 1:
                    var busy = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") };
                    busy.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(5));
                    return busy;
                case 2:
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("down") };
                case 3:
                    throw new HttpRequestException("The connection was reset (test).");
                default:
                    return null;
            }
        };

        string path = store.Combine(store.Root, "a.txt");
        Put(store, path, [1, 2, 3]);
        Assert.Equal(new byte[] { 1, 2, 3 }, ReadAll(store, path));
        Assert.True(troubles > 3);
    }

    [Theory]
    [MemberData(nameof(TestDestinations.Clouds), MemberType = typeof(TestDestinations))]
    public void An_expired_token_is_renewed_and_a_revoked_access_is_named(string kind)
    {
        var destination = TestDestinations.Create(kind, _platform, _temp);
        var cloud = _platform.Clouds[destination.CredentialId!];
        using (var store = _platform.OpenStore(destination))
        {
            store.CreateFolder(store.Root);

            // The token the store holds ran out: a new one is asked for, and the request goes through.
            cloud.NewToken();
            Assert.Empty(store.List(store.Root));
        }

        cloud.Revoked = true;
        cloud.NewToken();
        using var again = _platform.OpenStore(destination);
        var failure = Assert.ThrowsAny<StoreException>(() => again.List(again.Root));
        Assert.IsType<CloudAccessRevokedException>(failure);
        Assert.Equal(Messages.Format(Messages.CloudAccessRevoked, "backup@example.org", "test"), failure.Message);
    }

    [Theory]
    [InlineData(TestDestinations.GDrive, "backup@example.org")]
    [InlineData(TestDestinations.YaDisk, "backup.office")]
    [InlineData(TestDestinations.Dropbox, "backup@example.org")]
    public void Space_and_the_name_of_the_account_come_from_the_drive(string kind, string name)
    {
        var destination = TestDestinations.Create(kind, _platform, _temp);
        var cloud = _platform.Clouds[destination.CredentialId!];
        cloud.Total = 2_000_000_000;
        cloud.Used = 500_000_000;
        using var store = (CloudStore)_platform.OpenStore(destination);
        Assert.Equal(new StoreSpace(2_000_000_000, 1_500_000_000), store.GetSpace(store.Root));
        Assert.Equal(name, store.AccountName());
        Assert.Equal(CloudStore.LabelOf(destination.Kind, destination.Path), store.Label);
        Assert.StartsWith(CloudService.NameOf(destination.Kind) + ": " + CloudService.AppFolderOf(destination.Kind) + "/office/", store.Label, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(TestDestinations.Clouds), MemberType = typeof(TestDestinations))]
    public void An_archive_in_parts_is_numbered_and_restored_as_one(string kind)
    {
        string source = _temp.Combine("Data");
        byte[] big = RandomBytes(2_500_000);
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "big.bin"), big);
        _temp.WriteFile("Data/a.txt", "alpha", Modified);
        var destination = TestDestinations.Create(kind, _platform, _temp);
        destination.PartMb = 1;
        var task = new TaskDefinition
        {
            Name = "Docs",
            BackupType = BackupTypes.Full,
            Sources = [new TaskSource { Path = source }],
            Destinations = [destination],
            Archive = new ArchiveOptions { Format = ArchiveFormats.Zip, Level = 1 },
            Verify = new VerifyOptions { EveryRuns = 1 },
            Vss = new VssOptions { Enabled = false },
            Copy = new CopyOptions { ReadRetries = 0 },
        };

        var engine = new BackupEngine(_platform, _state, _log);
        using (var gate = new PauseGate())
        {
            var result = engine.Run(
                new BackupRequest { Task = task, RunId = "r1", Trigger = RunTriggers.Manual, StartedAt = Start, TimeZone = TimeZoneInfo.Utc },
                gate,
                TestContext.Current.CancellationToken);
            Assert.Equal(RunStatuses.Success, result.Status);
            Assert.True(result.Verify.Performed);
        }

        using var store = _platform.OpenStore(destination);
        var names = store.List(store.Root).Select(e => e.Name).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["Docs 2026-11-01 10-00 (Full).zip.001", "Docs 2026-11-01 10-00 (Full).zip.002", "Docs 2026-11-01 10-00 (Full).zip.003"], names);
        Assert.Equal(1024 * 1024, store.GetFileSize(store.Combine(store.Root, names[0])));

        var access = new ArchiveAccess(_platform.Archives, null, store);
        var set = Assert.Single(SetCatalog.Scan(store, store.Root, task.Id, access));
        Assert.Equal(SetStorage.Archive, set.Storage);
        Assert.Equal(ArchiveFormats.Zip, set.Format);
        string target = _temp.Combine("Restored");
        var restored = new RestoreEngine(_log).Run(
            new RestoreRequest { Sets = [set], Set = set, TargetFolder = target, Existing = RestoreExisting.Overwrite, Access = access },
            TestContext.Current.CancellationToken);
        Assert.Equal(RunStatuses.Success, restored.Status);
        Assert.Equal(big, File.ReadAllBytes(Path.Combine(target, "Data", "big.bin")));
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(target, "Data", "a.txt")));
    }

    [Fact]
    public void Parts_are_only_for_cloud_drives_and_a_mirror_never_goes_there()
    {
        var store = new ConfigStore(_temp.Combine("config"), new XorProtector());
        store.Load();
        var account = store.SaveCloudAccount(null, CredentialKinds.GDrive, "backup@example.org", "refresh-1", "app-1");
        var task = new TaskDefinition
        {
            Name = "Docs",
            Sources = [new TaskSource { Path = @"C:\Data" }],
            Destinations =
            [
                new TaskDestination { Kind = DestinationKinds.GDrive, Path = "office", CredentialId = account.Id, PartMb = 500 },
                new TaskDestination { Kind = DestinationKinds.Local, Path = @"D:\Backup", PartMb = 500 },
            ],
        };

        var saved = store.SaveTask(task, null, "local:a", Now).Saved;
        Assert.Equal([500, 0], saved.Destinations.Select(d => d.PartMb));

        saved.BackupType = BackupTypes.Mirror;
        saved.MirrorMode = MirrorModes.Exact;
        var mirror = Assert.Throws<ConfigException>(() => store.SaveTask(saved, saved.Revision, "local:a", Now));
        Assert.Contains(Messages.Format(Messages.CloudNoMirror, CloudStore.LabelOf(DestinationKinds.GDrive, "office")), mirror.Message, StringComparison.Ordinal);

        // An account of another service does not fit.
        var dropbox = store.SaveCloudAccount(null, CredentialKinds.Dropbox, "backup@example.org", "refresh-2", "app-2");
        saved.BackupType = BackupTypes.Full;
        saved.MirrorMode = null;
        saved.Destinations[0].CredentialId = dropbox.Id;
        Assert.Throws<ConfigException>(() => store.SaveTask(saved, saved.Revision, "local:a", Now));
    }

    [Fact]
    public void A_cloud_account_is_made_on_this_computer_only_and_connecting_again_keeps_it()
    {
        var store = new ConfigStore(_temp.Combine("config"), new XorProtector());
        store.Load();
        var account = store.SaveCloudAccount(null, CredentialKinds.YaDisk, "backup.office", "refresh-1", "app-1");
        Assert.Equal((CredentialKinds.YaDisk, "backup.office", "app-1"), (account.Kind, account.User, account.ClientId));
        Assert.Equal("refresh-1", store.GetPassword(account.Id));

        // Connected again (another sign-in, maybe another login): the same account for the tasks, a new token.
        var again = store.SaveCloudAccount(account.Id, CredentialKinds.YaDisk, "backup.office2", "refresh-2", "app-1");
        Assert.Equal(account.Id, again.Id);
        Assert.Equal("backup.office2", again.User);
        Assert.Equal("refresh-2", store.GetPassword(account.Id));
        store.UpdateCloudToken(account.Id, "refresh-3");
        var reloaded = new ConfigStore(_temp.Combine("config"), new XorProtector());
        reloaded.Load();
        Assert.Equal("refresh-3", reloaded.GetPassword(account.Id));

        // Typed in or sent from the server: refused.
        var typed = new Credential { Id = Guid.NewGuid().ToString(), Kind = CredentialKinds.GDrive, User = "someone@example.org" };
        Assert.Throws<ConfigException>(() => store.SaveCredential(typed, "token"));
        var replaced = new Credential { Id = account.Id, Kind = CredentialKinds.YaDisk, User = "backup.office" };
        Assert.Throws<ConfigException>(() => store.SaveCredential(replaced, "token"));
        Assert.Equal("refresh-3", store.GetPassword(account.Id));
    }

    [Fact]
    public void The_sign_in_page_asks_for_a_code_with_pkce_and_the_answer_is_read_back()
    {
        var sign = CloudAuthorization.Begin(CloudService.Dropbox, new CloudApp { ClientId = "app-id" }, "http://localhost:47901/");
        var query = QueryOf(new Uri(sign.Url));
        Assert.StartsWith("https://www.dropbox.com/oauth2/authorize?", sign.Url, StringComparison.Ordinal);
        Assert.Equal("app-id", query["client_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(sign.State, query["state"]);
        Assert.Equal("http://localhost:47901/", query["redirect_uri"]);
        Assert.Equal("offline", query["token_access_type"]);
        Assert.Equal(43, query["code_challenge"].Length);

        Assert.Equal("abc", sign.ReadCode("http://localhost:47901/?code=abc&state=" + Uri.EscapeDataString(sign.State)));
        Assert.Equal("pasted-code", sign.ReadCode("  pasted-code \r\n"));
        Assert.Null(sign.ReadCode(" "));
        var refused = Assert.Throws<StoreException>(() => sign.ReadCode("http://localhost:47901/?error=access_denied&state=" + sign.State));
        Assert.Equal(Messages.Format(Messages.CloudSignInRefused, Messages.CloudDropbox), refused.Message);
        Assert.Throws<StoreException>(() => sign.ReadCode("http://localhost:47901/?code=abc&state=forged"));

        // Without a port of its own the service shows the code: Yandex on its page, Dropbox without a redirect.
        var yandex = CloudAuthorization.Begin(CloudService.YaDisk, new CloudApp { ClientId = "y" }, null);
        Assert.Equal("https://oauth.yandex.ru/verification_code", QueryOf(new Uri(yandex.Url))["redirect_uri"]);
        var dropbox = CloudAuthorization.Begin(CloudService.Dropbox, new CloudApp { ClientId = "d" }, null);
        Assert.DoesNotContain("redirect_uri", QueryOf(new Uri(dropbox.Url)).Keys);
    }

    [Fact]
    public void The_code_is_traded_for_tokens_with_the_verifier_and_the_secret()
    {
        var sign = CloudAuthorization.Begin(CloudService.GDrive, new CloudApp { ClientId = "g-id", ClientSecret = "g-secret" }, "http://127.0.0.1:50123/");
        string challenge = QueryOf(new Uri(sign.Url))["code_challenge"];
        var token = new TokenServer(_ => (HttpStatusCode.OK, "{\"access_token\":\"a1\",\"refresh_token\":\"r1\",\"expires_in\":3599}"));
        var grant = sign.Finish("the-code", token, TestContext.Current.CancellationToken);

        Assert.Equal(("a1", "r1"), (grant.AccessToken, grant.RefreshToken));
        var form = Assert.Single(token.Forms);
        Assert.Equal("https://oauth2.googleapis.com/token", token.Addresses.Single());
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("the-code", form["code"]);
        Assert.Equal("g-id", form["client_id"]);
        Assert.Equal("g-secret", form["client_secret"]);
        Assert.Equal("http://127.0.0.1:50123/", form["redirect_uri"]);
        Assert.Equal(challenge, CloudAuthorization.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]))));

        // A grant without a refresh token would stop working within the hour.
        var again = CloudAuthorization.Begin(CloudService.GDrive, new CloudApp { ClientId = "g-id", ClientSecret = "g-secret" }, "http://127.0.0.1:50123/");
        var short_ = new TokenServer(_ => (HttpStatusCode.OK, "{\"access_token\":\"a1\",\"expires_in\":3599}"));
        var failure = Assert.Throws<StoreException>(() => again.Finish("the-code", short_, TestContext.Current.CancellationToken));
        Assert.Equal(Messages.Format(Messages.CloudNoRefreshToken, Messages.CloudGDrive), failure.Message);
    }

    [Fact]
    public void A_refresh_token_gives_access_tokens_and_a_new_refresh_token_is_kept()
    {
        string id = Guid.NewGuid().ToString();
        string? kept = null;
        int number = 0;
        var token = new TokenServer(form =>
        {
            number++;
            return (HttpStatusCode.OK, "{\"access_token\":\"a" + number + "\",\"refresh_token\":\"r" + (number + 1) + "\",\"expires_in\":3600}");
        });
        var tokens = new OAuthTokens(id, "backup.office", CloudService.YaDisk, new CloudApp { ClientId = "y-id", ClientSecret = "y-secret" }, "r1", r => kept = r, token);

        Assert.Equal("a1", tokens.AccessToken(renew: false, TestContext.Current.CancellationToken));
        Assert.Equal("a1", tokens.AccessToken(renew: false, TestContext.Current.CancellationToken));
        Assert.Equal("r2", kept);
        Assert.Equal("a2", tokens.AccessToken(renew: true, TestContext.Current.CancellationToken));
        Assert.Equal(["r1", "r2"], token.Forms.Select(f => f["refresh_token"]));
        Assert.All(token.Forms, f => Assert.Equal(("refresh_token", "y-id", "y-secret"), (f["grant_type"], f["client_id"], f["client_secret"])));
        OAuthTokens.Forget(id);
    }

    [Fact]
    public void A_refused_refresh_token_is_a_revoked_access()
    {
        string id = Guid.NewGuid().ToString();
        var token = new TokenServer(_ => (HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\",\"error_description\":\"Token has been expired or revoked.\"}"));
        var tokens = new OAuthTokens(id, "backup@example.org", CloudService.GDrive, new CloudApp { ClientId = "g", ClientSecret = "s" }, "r1", null, token);
        var failure = Assert.Throws<CloudAccessRevokedException>(() => tokens.AccessToken(renew: false, TestContext.Current.CancellationToken));
        Assert.Equal(Messages.Format(Messages.CloudAccessRevoked, "backup@example.org", Messages.CloudGDrive), failure.Message);

        // Without the application that issued the token nothing can be renewed, and that is said.
        var orphan = new OAuthTokens(Guid.NewGuid().ToString(), "backup@example.org", CloudService.GDrive, null, "r1", null, token);
        var missing = Assert.Throws<StoreException>(() => orphan.AccessToken(renew: false, TestContext.Current.CancellationToken));
        Assert.Equal(Messages.Format(Messages.CloudAppMissing, Messages.CloudGDrive), missing.Message);
    }

    [Fact]
    public void A_token_is_renewed_with_the_application_that_issued_it()
    {
        var own = new CloudAppsFile
        {
            Gdrive = new CloudApp { ClientId = "own-google", ClientSecret = "own-secret" },
            Dropbox = new CloudApp { ClientId = "own-dropbox" },
        };
        Assert.Equal("own-secret", CloudApps.ForToken(DestinationKinds.GDrive, "own-google", own)?.ClientSecret);
        Assert.Equal("own-google", CloudApps.ForConnect(DestinationKinds.GDrive, own)?.ClientId);

        // An application this computer no longer knows: Google cannot do without its secret, Dropbox can.
        Assert.Null(CloudApps.ForToken(DestinationKinds.GDrive, "forgotten", own));
        Assert.Equal("forgotten", CloudApps.ForToken(DestinationKinds.Dropbox, "forgotten", own)?.ClientId);
        Assert.Null(new CloudAppsFile().Of(DestinationKinds.YaDisk));
    }

    [Fact]
    public async Task The_browser_comes_back_to_a_loopback_port_with_the_code()
    {
        using var receiver = LoopbackReceiver.TryStart(0);
        Assert.NotNull(receiver);
        var wait = receiver.WaitAsync("Done, close the tab.", TestContext.Current.CancellationToken);
        using var http = new HttpClient();
        string root = "http://127.0.0.1:" + receiver.Port + "/";

        // The icon of the page is no answer.
        using (var icon = await http.GetAsync(root + "favicon.ico", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, icon.StatusCode);
        }

        Assert.False(wait.IsCompleted);
        string page = await http.GetStringAsync(root + "?code=abc%2Fdef&state=xyz", TestContext.Current.CancellationToken);
        Assert.Contains("Done, close the tab.", page, StringComparison.Ordinal);
        Assert.Equal("/?code=abc%2Fdef&state=xyz", await wait);

        // A port that another program holds: the code is entered by hand.
        using var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        Assert.Null(LoopbackReceiver.TryStart(((IPEndPoint)busy.LocalEndpoint).Port));
    }

    private static byte[] RandomBytes(int length)
    {
        byte[] data = new byte[length];
        new Random(length).NextBytes(data);
        return data;
    }

    private static byte[] Hash(DestinationStore store, string path)
    {
        using var stream = store.OpenRead(path);
        return SHA256.HashData(stream);
    }

    private static void Put(DestinationStore store, string path, byte[] data)
    {
        using var writer = store.BeginWrite(path, BackupEngine.TempSuffix);
        writer.Stream.Write(data);
        writer.Close();
        writer.Commit();
    }

    private static byte[] ReadAll(DestinationStore store, string path)
    {
        using var stream = store.OpenRead(path);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static Dictionary<string, string> QueryOf(Uri address) =>
        address.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : string.Empty, StringComparer.Ordinal);

    /// <summary>The token endpoint of a service: keeps what was posted and answers as told.</summary>
    private sealed class TokenServer(Func<Dictionary<string, string>, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
    {
        public List<Dictionary<string, string>> Forms { get; } = [];

        public List<string> Addresses { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            string body = request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            var form = body.Split('&').Select(p => p.Split('=', 2))
                .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')), StringComparer.Ordinal);
            Forms.Add(form);
            Addresses.Add(request.RequestUri!.AbsoluteUri);
            var (status, text) = answer(form);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }
}
