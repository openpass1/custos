using System.Diagnostics;
using System.Globalization;
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

/// <summary>
/// The whole protocol against a real CUSTOS Monitor (server/): registration, approval, heartbeat, the configuration
/// snapshot, run reports with a log, progress, the inventory, a log on request and a command. Runs only where
/// <c>CUSTOS_TEST_MONITOR</c> names the server: <c>url|registration code|sql</c>, where «sql» is a command that runs
/// the SQL on its input in the database of the server and prints the rows ("mysql -N -B -h127.0.0.1 -uroot -p... custos").
/// The test approves the agent and queues the command through it, as an administrator would on the site. The CI job
/// «monitor» starts a server with PHP's own web server (client/tools/ci-monitor.sh).
/// </summary>
public sealed class RealMonitorTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly FakeMonitorHost _host = new();
    private readonly ManualTime _time = new(DateTimeOffset.UtcNow);
    private readonly ConfigStore _config;
    private readonly LinkStore _store;
    private readonly AgentDatabase _database;
    private readonly MonitorClient _client = new();

    public RealMonitorTests()
    {
        _config = new ConfigStore(_temp.Combine("config"), new XorProtector());
        _config.Load();
        _store = new LinkStore(_temp.Combine("config"), new XorProtector());
        _store.Load();
        _database = new AgentDatabase(_temp.Combine("db", "custos.db"));
    }

    public void Dispose()
    {
        _client.Dispose();
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_agent_registers_reports_and_carries_out_a_command()
    {
        var server = Server();
        var now = DateTimeOffset.UtcNow;
        var share = _config.SaveCredential(new Credential { Kind = CredentialKinds.Windows, User = "backup", Domain = "OFFICE" }, "secret");
        var task = _config.SaveTask(new TaskDefinition
        {
            Name = "Documents",
            Sources = [new TaskSource { Path = @"C:\Users\Public\Documents" }],
            Destinations = [new TaskDestination { Kind = DestinationKinds.Unc, Path = @"\\nas\backup", CredentialId = share.Id }],
        }, null, @"local:TESTPC\admin", now).Saved;
        _host.ServerUrl = server.Url;
        _host.ConfigRevision = _config.ConfigRevision;
        _host.Snapshot = _config.BuildSnapshot;
        using var link = new ServerLink(_store, _database, _host, _client, _time);

        // Registration (A.3): pending until an administrator approves the agent.
        link.Connect(server.Code, newServer: true);
        await link.StepAsync(Token);
        string agentId = _store.Get().AgentId ?? throw new InvalidOperationException("Not registered: " + Describe(link));
        Assert.Equal(ServerLinkStates.Pending, link.Describe().State);
        await link.StepAsync(Token);
        Assert.Equal(ServerLinkStates.Pending, link.Describe().State);

        Sql(server, $"UPDATE agents SET status = 'approved', approved_at = UTC_TIMESTAMP() WHERE id = '{agentId}'");
        _time.Advance(TimeSpan.FromSeconds(61));
        await link.StepAsync(Token);
        Assert.True(link.Online, Describe(link));

        // The server asked for the configuration (A.6); it now has the task.
        Assert.Equal(1, _host.Snapshots);
        Assert.Equal(_config.ConfigRevision.ToString(CultureInfo.InvariantCulture), Sql(server, $"SELECT config_revision FROM agents WHERE id = '{agentId}'"));
        Assert.Equal("Documents", Sql(server, $"SELECT name FROM tasks WHERE agent_id = '{agentId}' AND id = '{task.Id}'"));
        string credentials = Sql(server, $"SELECT credentials FROM agents WHERE id = '{agentId}'");
        Assert.Contains(share.Id, credentials, StringComparison.Ordinal);
        Assert.DoesNotContain("secret\"", credentials.Replace("has_secret", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(1, _host.InventoryRequests);

        // A run that failed, with its whole log (A.5, A.7), and the inventory (A.8), through the queue.
        string log = _temp.Combine("run.log");
        File.WriteAllLines(log, Enumerable.Range(1, 300).Select(i => $"2026-11-12 09:00:{i % 60:00} [INFO] line {i}"));
        var record = new RunRecord
        {
            RunId = Guid.NewGuid().ToString(), TaskId = task.Id, TaskName = task.Name, BackupType = BackupTypes.Full,
            Trigger = RunTriggers.Schedule, ScheduledFor = now.AddMinutes(-1), StartedAt = now, FinishedAt = now.AddMinutes(2),
            Status = RunStatuses.Error, SourceFiles = 10, SourceBytes = 1000, FilesProcessed = 9, BytesWritten = 900, Errors = 1,
            ErrorMessage = @"\\nas\backup is not reachable",
            Destinations = [new DestinationResult { DestinationId = task.Destinations[0].Id, Status = RunStatuses.Error, Error = "not reachable" }],
            Verify = new RunVerify { Ok = true, Scope = VerifyScopes.New, SetsChecked = 1 },
        };
        link.Enqueue(RunReports.StartPath, JsonSerializer.SerializeToUtf8Bytes(RunReports.Start(record, task.Revision), MonitorJsonContext.Default.RunStartBody));
        link.Enqueue(RunReports.FinishPath, JsonSerializer.SerializeToUtf8Bytes(
            RunReports.Finish(record, new BackupResult { BackupType = BackupTypes.Full }, log), MonitorJsonContext.Default.RunFinishBody));
        var inventory = new InventoryBody
        {
            Destinations =
            [
                new InventoryDestination
                {
                    TaskId = task.Id, DestinationId = task.Destinations[0].Id, Kind = DestinationKinds.Unc, Path = @"\\nas\backup",
                    TotalBytes = 1_000_000_000, FreeBytes = 400_000_000,
                    Sets = [new InventorySet { Name = "Documents 2026-11-12 0900 full", Type = BackupTypes.Full, Time = now, Bytes = 900, VerifiedAt = now, VerifyOk = true }],
                },
            ],
        };
        link.Enqueue(ServerLink.InventoryPath, JsonSerializer.SerializeToUtf8Bytes(inventory, MonitorJsonContext.Default.InventoryBody), ServerLink.InventoryKey);
        await link.StepAsync(Token);

        Assert.Equal(0, _database.CountReports());
        Assert.DoesNotContain(_host.Events, e => e.Kind == LinkEvent.Error);
        Assert.Equal("error", Sql(server, $"SELECT status FROM runs WHERE id = '{record.RunId}'"));
        Assert.Equal("1", Sql(server, $"SELECT COUNT(*) FROM run_logs WHERE run_id = '{record.RunId}'"));
        Assert.Equal("1", Sql(server, $"SELECT COUNT(*) FROM storage_sets WHERE agent_id = '{agentId}' AND task_id = '{task.Id}'"));

        // Progress of a run in progress, and a log on request (log.fetch), sent directly.
        var second = new RunRecord
        {
            RunId = Guid.NewGuid().ToString(), TaskId = task.Id, TaskName = task.Name, BackupType = BackupTypes.Incremental,
            Trigger = RunTriggers.Remote, StartedAt = now.AddMinutes(5),
        };
        link.Enqueue(RunReports.StartPath, JsonSerializer.SerializeToUtf8Bytes(RunReports.Start(second, task.Revision), MonitorJsonContext.Default.RunStartBody));
        await link.StepAsync(Token);
        var progress = new RunProgressBody { RunId = second.RunId, Phase = BackupPhases.Copy, Percent = 42.5, FilesDone = 4, FilesTotal = 10, BytesDone = 400, BytesTotal = 1000, SpeedBps = 100, EtaS = 6, CurrentFile = @"C:\Users\Public\Documents\a.txt" };
        var answer = await link.SendAsync("agent/runs/progress", JsonSerializer.SerializeToUtf8Bytes(progress, MonitorJsonContext.Default.RunProgressBody), Token);
        Assert.True(answer.Ok, answer.Describe());
        answer = await link.SendAsync($"agent/runs/{record.RunId}/log",
            JsonSerializer.SerializeToUtf8Bytes(new RunLogBody { LogGzB64 = RunReports.Pack(log)! }, MonitorJsonContext.Default.RunLogBody), Token);
        Assert.True(answer.Ok, answer.Describe());

        // A command queued on the site comes with the next heartbeat; its result goes back (A.4).
        string commandId = Guid.NewGuid().ToString();
        Sql(server, "INSERT INTO commands (id, agent_id, kind, payload, created_at, expires_at) VALUES "
            + $"('{commandId}', '{agentId}', 'task.run', '{{\"task_id\": \"{task.Id}\"}}', UTC_TIMESTAMP(), UTC_TIMESTAMP() + INTERVAL 1 HOUR)");
        _time.Advance(TimeSpan.FromSeconds(61));
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(Token))
        {
            var loop = link.RunAsync(stop.Token);
            await Waiting.Until(() => Sql(server, $"SELECT status FROM commands WHERE id = '{commandId}'") == "done", "the command result on the server");
            await stop.CancelAsync();
            await loop;
        }

        Assert.Equal(CommandKinds.TaskRun, Assert.Single(_host.Executed).Kind);
        Assert.Equal(task.Id, _host.Executed[0].Payload.GetProperty("task_id").GetString());

        // A change made here goes 10 seconds later.
        task.Comment = "changed here";
        _config.SaveTask(task, task.Revision, @"local:TESTPC\admin", DateTimeOffset.UtcNow);
        _host.ConfigRevision = _config.ConfigRevision;
        link.ConfigChanged();
        _time.Advance(ServerLink.ConfigDelay);
        await link.StepAsync(Token);
        Assert.Equal(_config.ConfigRevision.ToString(CultureInfo.InvariantCulture), Sql(server, $"SELECT config_revision FROM agents WHERE id = '{agentId}'"));
        Assert.DoesNotContain(_host.Events, e => e.Kind == LinkEvent.Error);

        // «Сбросить привязку» keeps the agent key: connected again, the server knows the machine and its approval (A.3).
        link.Reset();
        link.Connect(server.Code, newServer: true);
        await link.StepAsync(Token);
        Assert.Equal(agentId, _store.Get().AgentId);
        await link.StepAsync(Token);
        Assert.True(link.Online, Describe(link));
    }

    [Fact]
    public async Task A_wrong_registration_key_is_refused_by_the_real_server()
    {
        var server = Server();
        _host.ServerUrl = server.Url;
        using var link = new ServerLink(_store, _database, _host, _client, _time);
        link.Connect("AAAAA-BBBBB-CCCCC-DDDDD-EEEEE", newServer: true);

        await link.StepAsync(Token);

        Assert.Equal(ServerLinkStates.KeyRejected, link.Describe().State);
        Assert.Null(_store.Get().AgentId);
    }

    private string Describe(ServerLink link) =>
        link.Describe().State + ": " + link.Describe().Detail + " | " + string.Join(" | ", _host.Events.Select(e => e.Text));

    private static string Sql(TestServer server, string sql)
    {
        var start = new ProcessStartInfo("bash", ["-c", server.SqlCommand])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        process.StandardInput.Write(sql + ";\n");
        process.StandardInput.Close();
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"SQL failed ({process.ExitCode}): {error}");
        return output.Trim();
    }

    private static TestServer Server()
    {
        string? value = Environment.GetEnvironmentVariable("CUSTOS_TEST_MONITOR");
        if (string.IsNullOrEmpty(value) && Environment.GetEnvironmentVariable("CUSTOS_REQUIRE_MONITOR") == "1")
        {
            Assert.Fail("CUSTOS_TEST_MONITOR is required here (CUSTOS_REQUIRE_MONITOR=1).");
        }

        Assert.SkipWhen(string.IsNullOrEmpty(value), "No monitoring server: CUSTOS_TEST_MONITOR is not set.");
        string[] parts = value!.Split('|', 3);
        Assert.True(parts.Length == 3, "CUSTOS_TEST_MONITOR must be url|code|sql command.");
        return new TestServer(parts[0].TrimEnd('/'), parts[1], parts[2]);
    }

    private sealed record TestServer(string Url, string Code, string SqlCommand);
}
