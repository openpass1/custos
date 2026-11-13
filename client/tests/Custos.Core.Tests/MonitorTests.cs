using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Custos.Core.Agent;
using Custos.Core.Backup;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Core.Monitor;
using Custos.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Custos.Core.Tests;

public sealed class RegistrationCodeTests
{
    [Fact]
    public void A_code_is_read_as_the_site_shows_it_or_typed_loosely()
    {
        var code = RegistrationCode.TryParse(" k7qhm-2xarv-4ntb6 ewz3p-lc5dq # 3f2a-91c0-77de-0b14 ", out string? problem);

        Assert.Null(problem);
        Assert.NotNull(code);
        Assert.Equal("K7QHM2XARV4NTB6EWZ3PLC5DQ", code.Key);
        Assert.Equal("3F2A91C077DE0B14", code.Fingerprint);
        Assert.Equal("K7QHM-2XARV-4NTB6-EWZ3P-LC5DQ#3F2A-91C0-77DE-0B14", code.Display());
        Assert.Null(RegistrationCode.TryParse("K7QHM-2XARV-4NTB6-EWZ3P-LC5DQ", out _)!.Fingerprint);
    }

    [Fact]
    public void The_connection_line_of_the_site_splits_into_the_address_and_the_code()
    {
        Assert.True(RegistrationCode.TrySplitConnection(" http://custos.example.lan/#K7QHM-2XARV-4NTB6-EWZ3P-LC5DQ#3F2A-91C0-77DE-0B14 ", out string url, out string code));
        Assert.Equal("http://custos.example.lan", url);
        Assert.Equal("K7QHM-2XARV-4NTB6-EWZ3P-LC5DQ#3F2A-91C0-77DE-0B14", code);
        Assert.NotNull(RegistrationCode.TryParse(code, out _));

        Assert.True(RegistrationCode.TrySplitConnection("HTTPS://custos.example.lan:8443#K7QHM2XARV4NTB6EWZ3PLC5DQ", out url, out code));
        Assert.Equal("HTTPS://custos.example.lan:8443", url);
        Assert.Equal("K7QHM2XARV4NTB6EWZ3PLC5DQ", code);

        // A bare code and a bare address are left as they are.
        Assert.False(RegistrationCode.TrySplitConnection("K7QHM-2XARV-4NTB6-EWZ3P-LC5DQ#3F2A-91C0-77DE-0B14", out _, out _));
        Assert.False(RegistrationCode.TrySplitConnection("https://custos.example.lan", out _, out _));
        Assert.False(RegistrationCode.TrySplitConnection(null, out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("K7QHM-2XARV-4NTB6-EWZ3P")]
    [InlineData("K7QHM-2XARV-4NTB6-EWZ3P-LC5D1")]
    [InlineData("K7QHM-2XARV-4NTB6-EWZ3P-LC5DQ#3F2A-91C0")]
    [InlineData("K7QHM-2XARV-4NTB6-EWZ3P-LC5DQ#3F2A-91C0-77DE-0B1X")]
    public void Wrong_codes_are_refused_with_a_reason(string text)
    {
        Assert.Null(RegistrationCode.TryParse(text, out string? problem));
        Assert.False(string.IsNullOrEmpty(problem));
    }
}

public sealed class MonitorCryptoTests
{
    private static readonly Lazy<RSA> Key = new(MonitorCrypto.NewKey);

    [Fact]
    public void A_secret_of_the_server_is_decrypted_and_a_changed_one_is_refused()
    {
        string encrypted = MonitorCrypto.EncryptSecret(Key.Value, "pa$$wörd");
        Assert.Equal("pa$$wörd", MonitorCrypto.DecryptSecret(Key.Value, encrypted));

        byte[] raw = Convert.FromBase64String(encrypted);
        raw[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => MonitorCrypto.DecryptSecret(Key.Value, Convert.ToBase64String(raw)));
        Assert.ThrowsAny<CryptographicException>(() => MonitorCrypto.DecryptSecret(Key.Value, "not base64!"));
    }

    [Fact]
    public void Signatures_hold_only_for_the_same_text_and_key()
    {
        string signature = MonitorCrypto.Sign(Key.Value, "200\n1760000000\nabc\nhash");
        using var publicKey = MonitorCrypto.LoadPublicPem(MonitorCrypto.PublicPem(Key.Value));

        Assert.True(MonitorCrypto.Verify(publicKey, "200\n1760000000\nabc\nhash", signature));
        Assert.False(MonitorCrypto.Verify(publicKey, "200\n1760000001\nabc\nhash", signature));
        Assert.False(MonitorCrypto.Verify(publicKey, "200\n1760000000\nabc\nhash", "garbage"));
        Assert.False(MonitorCrypto.Verify(null, "200\n1760000000\nabc\nhash", signature));
        Assert.Matches("^[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}$", MonitorCrypto.Fingerprint(Key.Value));
    }
}

public sealed class LinkStoreTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void The_binding_and_the_key_survive_a_restart_and_the_code_is_not_stored_plainly()
    {
        var store = new LinkStore(_temp.Combine("config"), new XorProtector());
        store.Load();
        string fingerprint = MonitorCrypto.Fingerprint(store.AgentKey);
        store.Update(s =>
        {
            s.RegistrationCodeDpapi = store.ProtectCode("K7QHM-2XARV-4NTB6-EWZ3P-LC5DQ");
            s.AgentId = "agent-1";
            s.Status = "approved";
        });
        store.NoteChanged(new TaskChangeNote { TaskId = "t1", Revision = 3, UpdatedBy = "local:admin" });
        store.NoteChanged(new TaskChangeNote { TaskId = "t1", Revision = 4, UpdatedBy = "local:admin" });
        store.NoteDeleted("t2");

        var reloaded = new LinkStore(_temp.Combine("config"), new XorProtector());
        reloaded.Load();

        Assert.Equal(fingerprint, MonitorCrypto.Fingerprint(reloaded.AgentKey));
        Assert.Equal("agent-1", reloaded.Get().AgentId);
        Assert.Equal("K7QHM-2XARV-4NTB6-EWZ3P-LC5DQ", reloaded.GetCode());
        Assert.Equal(4, Assert.Single(reloaded.Get().Changed).Revision);
        Assert.Equal(["t2"], reloaded.Get().DeletedTaskIds);
        Assert.DoesNotContain("K7QHM", File.ReadAllText(_temp.Combine("config", LinkStore.FileName)), StringComparison.Ordinal);
        Assert.Empty(reloaded.Problems);
    }

    [Fact]
    public void A_new_key_means_registering_again()
    {
        var store = new LinkStore(_temp.Combine("config"), new XorProtector());
        store.Load();
        store.Update(s => s.AgentId = "agent-1");
        File.Delete(_temp.Combine("config", LinkStore.KeyFileName));

        var reloaded = new LinkStore(_temp.Combine("config"), new XorProtector());
        reloaded.Load();

        Assert.Null(reloaded.Get().AgentId);
    }

    [Fact]
    public void A_damaged_file_is_set_aside_and_reported()
    {
        Directory.CreateDirectory(_temp.Combine("config"));
        File.WriteAllText(_temp.Combine("config", LinkStore.FileName), "{ not json");

        var store = new LinkStore(_temp.Combine("config"), new XorProtector());
        store.Load();

        Assert.Null(store.Get().AgentId);
        Assert.NotEmpty(store.Problems);
        Assert.Single(Directory.GetFiles(_temp.Combine("config"), LinkStore.FileName + ".damaged-*"));
    }

    [Fact]
    public void A_snapshot_sent_clears_only_what_it_carried()
    {
        var store = new LinkStore(_temp.Combine("config"), new XorProtector());
        store.Load();
        store.NoteChanged(new TaskChangeNote { TaskId = "t1", Revision = 1 });
        var sent = store.Get();
        store.NoteChanged(new TaskChangeNote { TaskId = "t2", Revision = 1 });
        store.NoteChanged(new TaskChangeNote { TaskId = "t1", Revision = 2 });

        store.SnapshotSent(sent);

        Assert.Equal(["t1", "t2"], store.Get().Changed.Select(c => c.TaskId).Order(StringComparer.Ordinal));
        Assert.Equal(2, store.Get().Changed.Single(c => c.TaskId == "t1").Revision);
    }
}

public sealed class OutboxTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly AgentDatabase _database;

    public OutboxTests()
    {
        _database = new AgentDatabase(_temp.Combine("db", "custos.db"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    [Fact]
    public void Reports_keep_their_order_and_a_key_keeps_only_the_newest()
    {
        _database.AddReport("agent/runs/start", "a"u8.ToArray());
        _database.AddReport(ServerLink.InventoryPath, "inv-1"u8.ToArray(), ServerLink.InventoryKey);
        _database.AddReport("agent/runs/finish", "b"u8.ToArray());
        _database.AddReport(ServerLink.InventoryPath, "inv-2"u8.ToArray(), ServerLink.InventoryKey);

        var reports = _database.GetReports(10);
        Assert.Equal(["a", "b", "inv-2"], reports.Select(r => Encoding.UTF8.GetString(r.Body)));

        _database.ReplaceReport(reports[1].Seq, "b2"u8.ToArray());
        _database.DeleteReport(reports[0].Seq);
        Assert.Equal(["b2", "inv-2"], _database.GetReports(10).Select(r => Encoding.UTF8.GetString(r.Body)));
        Assert.Equal(2, _database.CountReports());

        _database.ClearReports();
        Assert.Equal(0, _database.CountReports());
    }

    [Fact]
    public void Set_checks_are_kept_per_set_and_forgotten_with_the_task()
    {
        var at = new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
        _database.SaveSetChecks("task", [new SetCheck("dest-a", "Set 1", true), new SetCheck("dest-a", "Set 2", false)], at);
        _database.SaveSetChecks("task", [new SetCheck("DEST-A", "Set 1", false)], at.AddDays(1));

        var checks = _database.GetSetChecks("task");
        Assert.Equal((at.AddDays(1), false), checks[("DEST-A", "Set 1")]);
        Assert.Equal((at, false), checks[("DEST-A", "Set 2")]);

        _database.ForgetTask("task");
        Assert.Empty(_database.GetSetChecks("task"));
    }

    [Fact]
    public void The_verification_of_a_run_is_kept_in_the_history()
    {
        var start = new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
        _database.SaveRun(new RunRecord
        {
            RunId = "run-1", TaskId = "task", TaskName = "Docs", BackupType = BackupTypes.Full, Trigger = RunTriggers.Manual,
            StartedAt = start, FinishedAt = start.AddMinutes(1), Status = RunStatuses.Success,
            Verify = new RunVerify { Ok = true, Scope = VerifyScopes.New, SetsChecked = 1 },
        });
        _database.SaveRun(new RunRecord
        {
            RunId = "run-2", TaskId = "task", TaskName = "Docs", BackupType = BackupTypes.Full, Trigger = RunTriggers.Manual,
            StartedAt = start.AddHours(1), FinishedAt = start.AddHours(1).AddMinutes(1), Status = RunStatuses.Error,
            Verify = new RunVerify { Ok = false, Scope = VerifyScopes.Stored, SetsChecked = 3, FailedSets = ["Set 2"] },
        });
        _database.SaveRun(new RunRecord
        {
            RunId = "run-3", TaskId = "task", TaskName = "Docs", BackupType = BackupTypes.Full, Trigger = RunTriggers.Manual,
            StartedAt = start.AddHours(2), FinishedAt = start.AddHours(2).AddMinutes(1), Status = RunStatuses.Success,
        });

        var history = _database.GetHistory("task", 10).ToDictionary(r => r.RunId);

        Assert.True(history["run-1"].Verify!.Ok);
        Assert.Equal(VerifyScopes.New, history["run-1"].Verify!.Scope);
        Assert.False(history["run-2"].Verify!.Ok);
        Assert.Equal(["Set 2"], history["run-2"].Verify!.FailedSets);
        Assert.Equal(3, history["run-2"].Verify!.SetsChecked);
        Assert.Null(history["run-3"].Verify);
    }
}

public sealed class RunReportsTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void A_failed_run_sends_its_whole_log_and_a_good_one_only_the_tail()
    {
        string log = _temp.Combine("run.log");
        File.WriteAllLines(log, Enumerable.Range(1, 250).Select(i => $"line {i}"));

        var failed = RunReports.Finish(Record(RunStatuses.Error), new BackupResult { BackupType = BackupTypes.Full }, log);
        var good = RunReports.Finish(Record(RunStatuses.Success), new BackupResult { BackupType = BackupTypes.Full }, log);

        string[] tail = failed.LogTail!.Split('\n');
        Assert.Equal(RunReports.TailLines, tail.Length);
        Assert.Equal("line 151", tail[0]);
        Assert.Equal("line 250", tail[^1]);
        using (var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(failed.LogGzB64!)), CompressionMode.Decompress))
        using (var reader = new StreamReader(gzip))
        {
            Assert.Equal(File.ReadAllText(log), reader.ReadToEnd());
        }

        Assert.Null(good.LogGzB64);
        Assert.Equal(failed.LogTail, good.LogTail);
        Assert.Null(RunReports.Pack(log, maxPacked: 10));
        Assert.Null(RunReports.Tail(_temp.Combine("missing.log"), 10));
    }

    [Fact]
    public void The_verification_and_the_trigger_go_as_the_protocol_says()
    {
        var record = Record(RunStatuses.Success);
        record.Verify = new RunVerify { Ok = true, Scope = VerifyScopes.Stored, SetsChecked = 4 };
        var finish = RunReports.Finish(record, new BackupResult { BackupType = BackupTypes.Full }, null);
        var json = JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(finish, MonitorJsonContext.Default.RunFinishBody))!;

        Assert.True(json["verify"]!["performed"]!.GetValue<bool>());
        Assert.Equal("stored", json["verify"]!["scope"]!.GetValue<string>());
        Assert.Equal(4, json["verify"]!["sets_checked"]!.GetValue<int>());
        Assert.Null(json["log_tail"]);

        var unverified = RunReports.Finish(Record(RunStatuses.Success), new BackupResult { BackupType = BackupTypes.Full }, null);
        Assert.False(unverified.Verify.Performed);

        var manual = RunReports.Start(Record(null), 7);
        Assert.Null(manual.ScheduledFor);
        Assert.Equal(7, manual.TaskRevision);
    }

    private static RunRecord Record(string? status) => new()
    {
        RunId = "8e0b6f4c-0d55-4f38-9a49-3c9d2a1b0c11",
        TaskId = "2f6c1f9e-7d1a-4c55-8f0e-1a2b3c4d5e6f",
        TaskName = "Docs",
        BackupType = BackupTypes.Full,
        Trigger = RunTriggers.Manual,
        ScheduledFor = Start,
        StartedAt = Start,
        FinishedAt = status is null ? null : Start.AddMinutes(5),
        Status = status,
    };
}

public sealed class ServerConfigTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly string TaskId = "2f6c1f9e-7d1a-4c55-8f0e-1a2b3c4d5e6f";
    private static readonly string CredentialId = "5d0b1c7e-3f3a-4d4e-9a8b-7c6d5e4f3a2b";

    private readonly TempFolder _temp = new();
    private readonly ConfigStore _store;

    public ServerConfigTests()
    {
        _store = new ConfigStore(_temp.Combine("config"), new XorProtector());
        _store.Load();
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void A_task_from_the_server_is_saved_with_its_account()
    {
        var change = _store.UpsertFromServer(TaskJson("From the site", "server:alice"), 0,
            [new ServerCredential(CredentialId, CredentialKinds.Windows, "backup", "OFFICE", "secret")], Now);

        Assert.Null(change.Previous);
        Assert.Null(change.Overwritten);
        var task = _store.GetTask(TaskId)!;
        Assert.Equal("From the site", task.Name);
        Assert.Equal(1, task.Revision);
        Assert.Equal("server:alice", task.UpdatedBy);
        Assert.Equal("secret", _store.GetPassword(CredentialId));

        var snapshot = JsonNode.Parse(_store.BuildSnapshot(new LinkState()))!;
        var credential = snapshot["credentials"]!.AsArray().Single()!;
        Assert.True(credential["has_secret"]!.GetValue<bool>());
        Assert.Null(credential["secret"]);
        Assert.DoesNotContain("secret\"", snapshot["credentials"]!.ToJsonString().Replace("has_secret", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(TaskId, snapshot["tasks"]![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void An_edit_over_a_newer_local_version_wins_and_the_local_one_is_kept_for_the_server()
    {
        _store.UpsertFromServer(TaskJson("v1", "server:alice"), 0, [], Now);
        var local = _store.GetTask(TaskId)!;
        local.Comment = "changed here";
        _store.SaveTask(local, 1, "local:admin", Now);

        var change = _store.UpsertFromServer(TaskJson("v2", "nobody"), 1, [], Now);

        Assert.NotNull(change.Overwritten);
        Assert.Equal("changed here", change.Overwritten.Comment);
        var saved = _store.GetTask(TaskId)!;
        Assert.Equal("v2", saved.Name);
        Assert.Equal(3, saved.Revision);
        Assert.Equal("server:site", saved.UpdatedBy);
    }

    [Fact]
    public void The_server_cannot_send_an_ssh_key_or_a_task_without_an_id()
    {
        Assert.Throws<ConfigException>(() => _store.UpsertFromServer(TaskJson("x", "server:a"), 0,
            [new ServerCredential(CredentialId, CredentialKinds.SshKey, "root", null, "key")], Now));

        var noId = JsonSerializer.SerializeToElement(new JsonObject { ["name"] = "x" }, TestJson.Default.JsonNode);
        Assert.Throws<ConfigException>(() => _store.UpsertFromServer(noId, 0, [], Now));
        Assert.Empty(_store.GetTasks());
    }

    [Fact]
    public void The_key_of_a_destination_server_is_confirmed_here_and_never_taken_from_the_site()
    {
        const string confirmed = "SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU";
        const string foreign = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var credential = new ServerCredential(CredentialId, CredentialKinds.Sftp, "backup", null, "secret");

        // A new task from the site: whatever key it names, the server is not trusted yet.
        _store.UpsertFromServer(SftpTaskJson("backup.example.org", foreign), 0, [credential], Now);
        Assert.Null(_store.GetTask(TaskId)!.Destinations[0].ServerFingerprint);

        // The administrator confirms the key on this computer («Test access»).
        var local = _store.GetTask(TaskId)!;
        local.Destinations[0].ServerFingerprint = confirmed;
        _store.SaveTask(local, local.Revision, "local:admin", Now);

        // An edit from the site that leaves the server alone keeps it, with or without the field.
        _store.UpsertFromServer(SftpTaskJson("backup.example.org", null), 2, [], Now);
        Assert.Equal(confirmed, _store.GetTask(TaskId)!.Destinations[0].ServerFingerprint);
        _store.UpsertFromServer(SftpTaskJson("BACKUP.example.org", foreign), 3, [], Now);
        Assert.Equal(confirmed, _store.GetTask(TaskId)!.Destinations[0].ServerFingerprint);

        // Another server has to be confirmed anew: neither the old key nor the one the site sends goes with it.
        _store.UpsertFromServer(SftpTaskJson("other.example.org", confirmed), 4, [], Now);
        Assert.Null(_store.GetTask(TaskId)!.Destinations[0].ServerFingerprint);
    }

    [Fact]
    public void Settings_from_the_server_never_touch_the_local_ones()
    {
        _store.SetServerUrl("https://custos.example.lan");
        long revision = _store.ConfigRevision;
        var changes = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["log_retention_days"] = 30,
            ["server_url"] = "https://evil.example.lan",
            ["allow_remote_edit"] = false,
        }, TestJson.Default.JsonNode);

        var changed = _store.UpdateSettingsFromServer(changes);

        Assert.Equal(["log_retention_days"], changed);
        Assert.Equal(30, _store.GetSettings().LogRetentionDays);
        Assert.Equal("https://custos.example.lan", _store.GetSettings().ServerUrl);
        Assert.True(_store.GetSettings().AllowRemoteEdit);
        Assert.True(_store.ConfigRevision > revision);

        var badHash = JsonSerializer.SerializeToElement(new JsonObject { ["program_password_hash"] = "plain-password" }, TestJson.Default.JsonNode);
        Assert.Throws<ConfigException>(() => _store.UpdateSettingsFromServer(badHash));
        string hash = "pbkdf2-sha256$200000$" + Convert.ToBase64String(new byte[16]) + "$" + Convert.ToBase64String(new byte[32]);
        Assert.True(ConfigStore.IsPasswordHash(hash));
    }

    [Fact]
    public void Deleted_tasks_are_listed_only_while_they_are_gone()
    {
        _store.UpsertFromServer(TaskJson("v1", "server:a"), 0, [], Now);
        var notes = new LinkState { DeletedTaskIds = [TaskId, "0b8f5a2e-1c3d-4e5f-8a9b-0c1d2e3f4a5b"] };

        var snapshot = JsonNode.Parse(_store.BuildSnapshot(notes))!;

        Assert.Equal(["0b8f5a2e-1c3d-4e5f-8a9b-0c1d2e3f4a5b"], snapshot["deleted_task_ids"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    private static JsonElement SftpTaskJson(string host, string? fingerprint) => JsonSerializer.SerializeToElement(new JsonObject
    {
        ["id"] = TaskId,
        ["name"] = "To a server",
        ["updated_by"] = "server:alice",
        ["enabled"] = false,
        ["destinations"] = new JsonArray(new JsonObject
        {
            ["id"] = "d1",
            ["kind"] = "sftp",
            ["host"] = host,
            ["path"] = "/srv/backup",
            ["credential_id"] = CredentialId,
            ["object_lock_days"] = 0,
            ["server_fingerprint"] = fingerprint,
        }),
    }, TestJson.Default.JsonNode);

    private static JsonElement TaskJson(string name, string updatedBy) => JsonSerializer.SerializeToElement(new JsonObject
    {
        ["id"] = TaskId,
        ["name"] = name,
        ["revision"] = 5,
        ["updated_by"] = updatedBy,
        ["enabled"] = false,
    }, TestJson.Default.JsonNode);
}

public sealed class ServerLinkTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly FakeMonitorServer _server = new();
    private readonly FakeMonitorHost _host = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
    private readonly LinkStore _store;
    private readonly AgentDatabase _database;
    private readonly MonitorClient _client;
    private readonly ServerLink _link;

    public ServerLinkTests()
    {
        _store = new LinkStore(_temp.Combine("config"), new XorProtector());
        _store.Load();
        _database = new AgentDatabase(_temp.Combine("db", "custos.db"));
        _client = new MonitorClient(_server);
        _link = new ServerLink(_store, _database, _host, _client, _time);
    }

    public void Dispose()
    {
        _link.Dispose();
        _client.Dispose();
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private string State => _link.Describe().State;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Registration_pins_the_server_key_and_waits_for_approval()
    {
        _link.Connect(_server.Code, newServer: true);

        await _link.StepAsync(Token);

        var register = Assert.Single(_server.To("agent/register"));
        Assert.True(register.SignatureValid);
        Assert.Equal("TESTPC", register.Json["hostname"]!.GetValue<string>());
        Assert.Equal(_server.AgentId, _store.Get().AgentId);
        Assert.True(MonitorCrypto.SameKey(MonitorCrypto.PublicPem(_server.ServerKey), _store.Get().ServerPublicKey));
        Assert.Equal(ServerLinkStates.Pending, State);
        Assert.Equal("Test office", _link.Describe().ServerName);

        await _link.StepAsync(Token);
        Assert.Single(_server.To("agent/heartbeat"));
        Assert.Equal(ServerLinkStates.Pending, State);
        Assert.Empty(_server.Reports());

        _server.Status = "approved";
        _time.Advance(TimeSpan.FromSeconds(61));
        await _link.StepAsync(Token);

        Assert.Equal(ServerLinkStates.Approved, State);
        Assert.True(_link.Online);
        Assert.Equal(1, _host.InventoryRequests);
        Assert.All(_server.Requests, r => Assert.True(r.SignatureValid));
    }

    [Fact]
    public async Task A_server_with_another_key_than_the_code_says_is_refused()
    {
        _link.Connect(new RegistrationCode(FakeMonitorServer.RegistrationKey, "0000111122223333").Display(), newServer: true);

        var wait = await _link.StepAsync(Token);

        Assert.Equal(ServerLinkStates.Refused, State);
        Assert.Null(_store.Get().AgentId);
        Assert.Null(_store.Get().ServerPublicKey);
        Assert.Equal(ServerLink.RegistrationRetry, wait);
    }

    [Fact]
    public async Task A_wrong_registration_key_is_shown_as_such()
    {
        _link.Connect("AAAAA-BBBBB-CCCCC-DDDDD-EEEEE", newServer: true);

        await _link.StepAsync(Token);

        Assert.Equal(ServerLinkStates.KeyRejected, State);
        Assert.Null(_store.Get().AgentId);
    }

    [Fact]
    public async Task Without_a_code_nothing_is_sent()
    {
        var wait = await _link.StepAsync(Token);

        Assert.Equal(ServerLinkStates.NoCode, State);
        Assert.Equal(Timeout.InfiniteTimeSpan, wait);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task An_answer_signed_by_another_key_is_not_believed()
    {
        await Approved();
        _server.SignWith = MonitorCrypto.NewKey();
        _server.Status = "disabled";
        _time.Advance(TimeSpan.FromSeconds(61));

        var wait = await _link.StepAsync(Token);

        Assert.Equal(ServerLinkStates.Offline, State);
        Assert.Equal("approved", _store.Get().Status);
        Assert.Equal(TimeSpan.FromMinutes(1), wait);
    }

    [Fact]
    public async Task Reports_go_in_order_and_wait_while_the_server_fails()
    {
        await Approved();
        _link.Enqueue("agent/runs/start", """{"n":1}"""u8.ToArray());
        _link.Enqueue("agent/runs/finish", """{"n":2}"""u8.ToArray());
        _link.Enqueue("agent/runs/start", """{"n":3}"""u8.ToArray());
        int failures = 0;
        _server.Reply = r => r.Body == """{"n":2}""" && failures++ < 2 ? FakeMonitorServer.Error(500, "server_error") : null;

        var wait = await _link.StepAsync(Token);
        Assert.Equal(TimeSpan.FromMinutes(1), wait);
        Assert.Equal(2, _database.CountReports());

        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(30), await _link.StepAsync(Token));
        _time.Advance(TimeSpan.FromSeconds(30));
        await _link.StepAsync(Token);
        Assert.Equal(2, _database.CountReports());
        _time.Advance(TimeSpan.FromMinutes(1));
        await _link.StepAsync(Token);

        Assert.Equal(0, _database.CountReports());
        Assert.Equal(["""{"n":1}""", """{"n":2}""", """{"n":2}""", """{"n":2}""", """{"n":3}"""], _server.Reports().Select(r => r.Body));
    }

    [Fact]
    public async Task A_report_the_server_calls_wrong_is_dropped_and_the_rest_go_on()
    {
        await Approved();
        _link.Enqueue("agent/runs/start", """{"n":1}"""u8.ToArray());
        _link.Enqueue("agent/runs/start", """{"n":2}"""u8.ToArray());
        _server.Reply = r => r.Body == """{"n":1}""" ? FakeMonitorServer.Error(400, "bad_request") : null;

        await _link.StepAsync(Token);

        Assert.Equal(0, _database.CountReports());
        Assert.Contains(_host.Events, e => e.Kind == LinkEvent.Error);
    }

    [Fact]
    public async Task A_report_too_large_goes_again_without_the_whole_log()
    {
        await Approved();
        _link.Enqueue("agent/runs/finish", """{"run_id":"r","log_tail":"end","log_gz_b64":"AAAA"}"""u8.ToArray());
        _server.Reply = r => r.Body.Contains("log_gz_b64", StringComparison.Ordinal) ? FakeMonitorServer.Error(413, "too_large") : null;

        await _link.StepAsync(Token);

        Assert.Equal(0, _database.CountReports());
        var last = _server.Reports().Last().Json;
        Assert.Null(last["log_gz_b64"]);
        Assert.Equal("end", last["log_tail"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_unsigned_413_from_a_proxy_drops_the_log_but_not_a_report_without_one()
    {
        await Approved();
        _link.Enqueue("agent/runs/finish", """{"run_id":"r","log_gz_b64":"AAAA"}"""u8.ToArray());
        _link.Enqueue("agent/runs/finish", """{"run_id":"s","log_gz_b64":null}"""u8.ToArray());
        _server.Reply = r => r.Body.Contains("\"s\"", StringComparison.Ordinal) || r.Body.Contains("AAAA", StringComparison.Ordinal)
            ? new FakeMonitorServer.Answer(413, "<html>413 Request Entity Too Large</html>", Signed: false) : null;

        await _link.StepAsync(Token);

        var left = Assert.Single(_database.GetReports(10));
        Assert.Contains("\"s\"", Encoding.UTF8.GetString(left.Body), StringComparison.Ordinal);
        Assert.Equal(ServerLinkStates.Offline, State);
    }

    [Fact]
    public async Task Rate_limiting_waits_as_long_as_the_server_says()
    {
        await Approved();
        _link.Enqueue("agent/runs/start", """{"n":1}"""u8.ToArray());
        _server.Reply = r => r.Path == "agent/runs/start" ? FakeMonitorServer.Error(429, "rate_limited", retryAfter: 120) : null;

        var wait = await _link.StepAsync(Token);

        Assert.Equal(TimeSpan.FromSeconds(120), wait);
        Assert.Equal(1, _database.CountReports());
    }

    [Fact]
    public async Task An_agent_the_server_forgot_registers_again_with_the_same_server_key()
    {
        await Approved();
        string firstId = _store.Get().AgentId!;
        _link.Enqueue("agent/runs/start", """{"n":1}"""u8.ToArray());
        _server.Reply = r => r.Path == "agent/heartbeat" && r.AgentId == firstId ? FakeMonitorServer.Error(404, "unknown_agent") : null;
        _time.Advance(TimeSpan.FromSeconds(61));

        await _link.StepAsync(Token);
        Assert.Null(_store.Get().AgentId);

        await _link.StepAsync(Token);
        await _link.StepAsync(Token);

        Assert.NotEqual(firstId, _store.Get().AgentId);
        Assert.True(Assert.Single(_server.To("agent/register")).SignatureValid);
        Assert.Equal(ServerLinkStates.Approved, State);
        Assert.Equal(0, _database.CountReports());
    }

    [Fact]
    public async Task Five_minutes_without_the_server_are_reported_once_and_so_is_its_return()
    {
        await Approved();
        _server.Down = true;
        var waits = new List<TimeSpan>();
        _time.Advance(TimeSpan.FromMinutes(1));
        for (int i = 0; i < 5; i++)
        {
            var wait = await _link.StepAsync(Token);
            waits.Add(wait);
            _time.Advance(wait);
        }

        // A.11: 1, 2, 5, 10, 10… minutes.
        Assert.Equal([1, 2, 5, 10, 10], waits.Select(w => (int)w.TotalMinutes));
        Assert.Single(_host.Events, e => e.Kind == LinkEvent.ConnectionLost);
        Assert.NotNull(_link.Describe().OfflineSince);

        _server.Down = false;
        _time.Advance(TimeSpan.FromMinutes(10));
        await _link.StepAsync(Token);

        Assert.Single(_host.Events, e => e.Kind == LinkEvent.ConnectionRestored);
        Assert.Equal(ServerLinkStates.Approved, State);
        Assert.Null(_link.Describe().OfflineSince);
    }

    [Fact]
    public async Task A_local_change_is_sent_ten_seconds_later_and_at_the_server_s_request_at_once()
    {
        await Approved();
        _link.ConfigChanged();

        await _link.StepAsync(Token);
        Assert.Empty(_server.To("agent/config"));

        _time.Advance(ServerLink.ConfigDelay);
        await _link.StepAsync(Token);
        Assert.Single(_server.To("agent/config"));

        _server.WantConfig = true;
        _time.Advance(TimeSpan.FromSeconds(61));
        await _link.StepAsync(Token);
        Assert.Equal(2, _server.To("agent/config").Count());
    }

    [Fact]
    public async Task Pending_and_disabled_agents_keep_their_reports()
    {
        await Approved();
        _server.Status = "disabled";
        _link.Enqueue("agent/runs/start", """{"n":1}"""u8.ToArray());

        _time.Advance(TimeSpan.FromSeconds(61));
        var wait = await _link.StepAsync(Token);

        Assert.Equal(ServerLinkStates.Disabled, State);
        // As often as the server says for a disabled agent (A.4).
        Assert.Equal(TimeSpan.FromMinutes(5), wait);
        Assert.Equal(1, _database.CountReports());
    }

    [Fact]
    public async Task Commands_run_once_and_the_wrong_ones_are_refused()
    {
        await Approved();
        _host.AllowRemoteEdit = false;
        var run = _server.Command(CommandKinds.TaskRun, new JsonObject { ["task_id"] = "t" });
        _server.Command(CommandKinds.TaskRun, new JsonObject { ["task_id"] = "t" }, id: run.Id);
        var expired = _server.Command(CommandKinds.TaskStop, expiresAt: _time.Now.AddMinutes(-1));
        var unknown = _server.Command("task.explode");
        var edit = _server.Command(CommandKinds.TaskDelete, new JsonObject { ["task_id"] = "t" });
        _server.Command(CommandKinds.TaskRun, id: "not-a-guid");
        _time.Advance(TimeSpan.FromSeconds(61));

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var loop = _link.RunAsync(stop.Token);
        await Waiting.Until(() => _server.Reports().Count(r => r.Path.EndsWith("/result", StringComparison.Ordinal)) >= 4, "four command results");
        await stop.CancelAsync();
        await loop;

        Assert.Equal([run.Id], _host.Executed.Select(c => c.Id));
        var results = _server.Reports().Where(r => r.Path.EndsWith("/result", StringComparison.Ordinal))
            .ToDictionary(r => r.Path.Split('/')[2], r => r.Json["status"]!.GetValue<string>());
        Assert.Equal(CommandStatuses.Done, results[run.Id]);
        Assert.Equal(CommandStatuses.Rejected, results[expired.Id]);
        Assert.Equal(CommandStatuses.Rejected, results[unknown.Id]);
        Assert.Equal(CommandStatuses.Rejected, results[edit.Id]);
        Assert.Equal(4, results.Count);
    }

    [Fact]
    public async Task Resetting_forgets_the_server_and_the_queue()
    {
        await Approved();
        _link.Enqueue("agent/runs/start", """{"n":1}"""u8.ToArray());
        _host.ServerUrl = null;

        _link.Reset();

        Assert.Null(_store.Get().AgentId);
        Assert.Null(_store.Get().ServerPublicKey);
        Assert.Null(_store.GetCode());
        Assert.Equal(0, _database.CountReports());
        Assert.Equal(ServerLinkStates.Off, State);
    }

    /// <summary>Registered and approved, with the first inventory requested and nothing else pending.</summary>
    private async Task Approved()
    {
        _server.Status = "approved";
        _link.Connect(_server.Code, newServer: true);
        await _link.StepAsync(Token);
        await _link.StepAsync(Token);
        Assert.Equal(ServerLinkStates.Approved, State);
        _server.Requests.Clear();
    }
}
