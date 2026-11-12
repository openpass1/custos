using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Custos.Core.Agent;
using Custos.Core.Monitor;

namespace Custos.Core.Tests;

/// <summary>
/// A monitoring server in memory (appendix A): checks the signatures of the agent, signs its answers with its own key
/// and records every request. Enough of the protocol for <see cref="ServerLink"/>; <see cref="Reply"/> replaces any answer.
/// </summary>
internal sealed class FakeMonitorServer : HttpMessageHandler
{
    public const string Url = "https://custos.example.lan";

    /// <summary>The registration key of the server (A.3); 25 characters of base32.</summary>
    public const string RegistrationKey = "K7QHM2XARV4NTB6EWZ3PLC5DQ";

    private static readonly Lazy<RSA> SharedKey = new(MonitorCrypto.NewKey);
    private readonly Lock _lock = new();
    private readonly List<MonitorCommand> _commands = [];

    public RSA ServerKey { get; } = SharedKey.Value;

    /// <summary>Signs the answers with this key instead (a forged server).</summary>
    public RSA? SignWith { get; set; }

    /// <summary>No answer at all (a network failure).</summary>
    public bool Down { get; set; }

    public string Status { get; set; } = "pending";

    public bool WantConfig { get; set; }

    public string? AgentId { get; private set; }

    public string? AgentPublicPem { get; private set; }

    /// <summary>Answers a request instead of the usual handling; null = handled as usual.</summary>
    public Func<Request, Answer?>? Reply { get; set; }

    public List<Request> Requests { get; } = [];

    /// <summary>The code as the site shows it: the key and the fingerprint of the server key.</summary>
    public string Code => new RegistrationCode(RegistrationKey, MonitorCrypto.NormalizeFingerprint(MonitorCrypto.Fingerprint(ServerKey))).Display();

    public IEnumerable<Request> To(string path)
    {
        lock (_lock)
        {
            return [.. Requests.Where(r => r.Path == path)];
        }
    }

    public IEnumerable<Request> Reports()
    {
        lock (_lock)
        {
            return [.. Requests.Where(r => r.Path is not ("agent/register" or "agent/heartbeat"))];
        }
    }

    /// <summary>Queues a command for the next heartbeat of an approved agent.</summary>
    public MonitorCommand Command(string kind, object? payload = null, DateTimeOffset? expiresAt = null, string? id = null)
    {
        var command = new MonitorCommand
        {
            Id = id ?? Guid.NewGuid().ToString(),
            Kind = kind,
            Payload = JsonSerializer.SerializeToElement(payload ?? new JsonObject(), TestJson.Default.JsonNode),
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddHours(1),
        };
        lock (_lock)
        {
            _commands.Add(command);
        }

        return command;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Down)
        {
            throw new HttpRequestException("Connection refused (test)");
        }

        string path = request.RequestUri!.AbsolutePath["/api/v1/".Length..];
        byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        string time = Header(request, "X-Custos-Time");
        string nonce = Header(request, "X-Custos-Nonce");
        string signed = MonitorCrypto.RequestString(request.Method.Method, path, time, nonce, body);

        Answer answer;
        Request received;
        lock (_lock)
        {
            string? pem = path == "agent/register" ? JsonNode.Parse(body)?["public_key"]?.GetValue<string>() : AgentPublicPem;
            using var agentKey = MonitorCrypto.LoadPublicPem(pem);
            received = new Request(request.Method.Method, path, Encoding.UTF8.GetString(body), MonitorCrypto.Verify(agentKey, signed, Header(request, "X-Custos-Signature")), Header(request, "X-Custos-Agent"));
            Requests.Add(received);
            answer = Reply?.Invoke(received) ?? Handle(received, request, signed, pem);
        }

        var response = new HttpResponseMessage((HttpStatusCode)answer.Status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(answer.Body)) };
        if (answer.Signed)
        {
            string now = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            string responseString = MonitorCrypto.ResponseString(answer.Status, now, nonce, Encoding.UTF8.GetBytes(answer.Body));
            response.Headers.Add("X-Custos-Time", now);
            response.Headers.Add("X-Custos-Signature", MonitorCrypto.Sign(SignWith ?? ServerKey, responseString));
            if (path == "agent/register")
            {
                response.Headers.Add("X-Custos-Registration-Mac", MonitorCrypto.Hmac(answer.MacKey ?? RegistrationKey, responseString));
            }
        }

        return response;
    }

    public static Answer Error(int status, string error, string message = "test", int? retryAfter = null) =>
        new(status, retryAfter is { } seconds
            ? $$"""{"error":"{{error}}","message":"{{message}}","retry_after_s":{{seconds}}}"""
            : $$"""{"error":"{{error}}","message":"{{message}}"}""");

    private Answer Handle(Request received, HttpRequestMessage request, string signed, string? pem)
    {
        if (!received.SignatureValid)
        {
            return Error(401, "bad_signature");
        }

        if (received.Path == "agent/register")
        {
            if (!MonitorCrypto.HmacValid(RegistrationKey, signed, Header(request, "X-Custos-Registration-Mac")))
            {
                return Error(403, "bad_registration_key") with { MacKey = "WRONGWRONGWRONGWRONGWRONG" };
            }

            AgentId = Guid.NewGuid().ToString();
            AgentPublicPem = pem;
            var answer = new JsonObject
            {
                ["agent_id"] = AgentId,
                ["status"] = Status,
                ["server_public_key"] = MonitorCrypto.PublicPem(ServerKey),
                ["server_name"] = "Test office",
            };
            return new Answer(200, answer.ToJsonString());
        }

        if (received.AgentId != AgentId)
        {
            return Error(404, "unknown_agent");
        }

        if (received.Path == "agent/heartbeat")
        {
            var commands = new JsonArray();
            if (Status == "approved")
            {
                foreach (var command in _commands)
                {
                    commands.Add(new JsonObject
                    {
                        ["id"] = command.Id,
                        ["kind"] = command.Kind,
                        ["payload"] = JsonNode.Parse(command.Payload.GetRawText()),
                        ["expires_at"] = command.ExpiresAt?.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                    });
                }

                _commands.Clear();
            }

            var beat = new JsonObject
            {
                ["status"] = Status,
                ["server_time"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                ["heartbeat_interval_s"] = Status == "disabled" ? 300 : 60,
                ["want_config"] = WantConfig && Status == "approved",
                ["commands"] = commands,
            };
            WantConfig = false;
            return new Answer(200, beat.ToJsonString());
        }

        return Status == "approved" ? new Answer(200, """{"ok":true}""") : Error(403, Status);
    }

    private static string Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.First() : string.Empty;

    public sealed record Request(string Method, string Path, string Body, bool SignatureValid, string AgentId)
    {
        public JsonNode Json => JsonNode.Parse(Body)!;
    }

    /// <param name="Signed">False: an answer without signature headers, like one from a proxy.</param>
    /// <param name="MacKey">The registration key the HMAC of a registration answer is made with.</param>
    public sealed record Answer(int Status, string Body, bool Signed = true, string? MacKey = null);
}

/// <summary>What the agent would do, recorded.</summary>
internal sealed class FakeMonitorHost : IMonitorHost
{
    private readonly Lock _lock = new();

    public string? ServerUrl { get; set; } = FakeMonitorServer.Url;

    public bool AllowRemoteEdit { get; set; } = true;

    public RunProgressBody? CurrentProgress { get; set; }

    /// <summary>The configuration snapshot; null = a minimal one.</summary>
    public Func<LinkState, byte[]>? Snapshot { get; set; }

    public long ConfigRevision { get; set; } = 1;

    public int InventoryRequests { get; private set; }

    public int Snapshots { get; private set; }

    /// <summary>How a command is carried out; default: done.</summary>
    public Func<MonitorCommand, CommandOutcome?> Execute { get; set; } = _ => CommandOutcome.Done();

    public List<MonitorCommand> Executed { get; } = [];

    public List<(LinkEvent Kind, string Text)> Events { get; } = [];

    public RegisterBody DescribeMachine(string publicKeyPem) => new()
    {
        Hostname = "TESTPC",
        MachineGuid = "6b2c3a51-8a3f-4e2b-9c41-0a1b2c3d4e5f",
        Os = "Windows 11 Pro 24H2 (build 26100)",
        AgentVersion = "0.3.0",
        TimeZone = "UTC",
        PublicKey = publicKeyPem,
    };

    public HeartbeatBody DescribeState() => new()
    {
        AgentVersion = "0.3.0",
        SevenzipVersion = "24.09",
        ConfigRevision = ConfigRevision,
        TimeZone = "UTC",
        ServiceStartedAt = new DateTimeOffset(2026, 11, 12, 8, 0, 0, TimeSpan.Zero),
    };

    public byte[] BuildConfigSnapshot(LinkState notes)
    {
        Snapshots++;
        if (Snapshot is not null)
        {
            return Snapshot(notes);
        }

        return Encoding.UTF8.GetBytes($$"""{"config_revision":1,"changed":[],"settings":{},"credentials":[],"tasks":[],"deleted_task_ids":[],"overwritten":[],"changed_count":{{notes.Changed.Count}}}""");
    }

    public RunProgressBody? Progress() => CurrentProgress;

    public void RequestInventory() => InventoryRequests++;

    public Task<CommandOutcome?> ExecuteAsync(MonitorCommand command, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Executed.Add(command);
        }

        return Task.FromResult(Execute(command));
    }

    public void Report(LinkEvent kind, string text)
    {
        lock (_lock)
        {
            Events.Add((kind, text));
        }
    }

    public void LinkChanged()
    {
    }

    public bool Has(LinkEvent kind)
    {
        lock (_lock)
        {
            return Events.Any(e => e.Kind == kind);
        }
    }
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Not encryption: enough to see that what is stored is not the plain text.</summary>
internal sealed class XorProtector : ISecretProtector
{
    public byte[] Protect(byte[] data) => [.. data.Select(b => (byte)(b ^ 0x5A))];

    public byte[] Unprotect(byte[] data) => Protect(data);
}

[System.Text.Json.Serialization.JsonSerializable(typeof(JsonNode))]
internal sealed partial class TestJson : System.Text.Json.Serialization.JsonSerializerContext;

internal static class Waiting
{
    /// <summary>Waits until <paramref name="condition"/> holds (things that happen on other threads), at most 15 s.</summary>
    public static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting for: " + what);
            }

            await Task.Delay(20);
        }
    }
}
