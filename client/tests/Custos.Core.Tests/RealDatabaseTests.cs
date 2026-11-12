using System.Text;
using Custos.Core.Archives;
using Custos.Core.Backup;
using Custos.Core.Databases;
using Custos.Core.Model;

namespace Custos.Core.Tests;

/// <summary>
/// Dumps from real servers through their own programs. Each engine runs only where a variable names its server:
/// <c>CUSTOS_TEST_MYSQL</c>, <c>CUSTOS_TEST_MARIADB</c>, <c>CUSTOS_TEST_POSTGRESQL</c> =
/// <c>folder of the programs|host|port|user|password</c>. The Linux job of the CI starts the servers in containers.
/// The server needs a database «shop» with a table «items» and a second user; see .github/workflows/build.yml.
/// </summary>
public sealed class RealDatabaseTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    private readonly TempFolder _temp = new();
    private readonly TestPlatform _platform = new();
    private readonly MemoryFileStateStore _store = new();
    private readonly TestLog _log = new();

    public void Dispose() => _temp.Dispose();

    public static TheoryData<string> Engines => [DbEngines.MySql, DbEngines.MariaDb, DbEngines.PostgreSql];

    [Theory]
    [MemberData(nameof(Engines))]
    public void The_server_lists_its_databases(string engine)
    {
        var (server, account) = Server(engine);

        var info = new ProcessDbClient(server, account).Inspect(TestContext.Current.CancellationToken);

        Assert.NotEmpty(info.Version);
        Assert.Contains("shop", info.Databases);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void Every_database_and_the_users_are_dumped_complete_and_restored_with_a_readme(string engine)
    {
        var (server, account) = Server(engine);
        _platform.DbAccounts["account"] = account;
        server.CredentialId = "account";
        string backup = _temp.Combine("Backup");
        var task = new TaskDefinition
        {
            Name = "Databases",
            BackupType = BackupTypes.Full,
            Destinations = [new TaskDestination { Kind = DestinationKinds.Local, Path = backup }],
            DbServers = [server],
            Vss = new VssOptions { Enabled = false },
        };

        var result = Run(task);

        Assert.True(result.Status == RunStatuses.Success, string.Join(Environment.NewLine, _log.Lines.Select(l => l.Message)));
        string set = Path.Combine(backup, result.SetName!);
        string folder = Path.Combine(set, "db", engine);
        string shop = File.ReadAllText(Path.Combine(folder, "shop.sql"));
        Assert.Contains("items", shop, StringComparison.Ordinal);
        Assert.True(DumpCheck.HasEndLine(engine, Encoding.UTF8.GetBytes(shop)));
        string users = File.ReadAllText(Path.Combine(folder, "_users.sql"));
        Assert.True(DumpCheck.HasEndLine(engine, Encoding.UTF8.GetBytes(users)), users);
        Assert.Contains("custos_reader", users, StringComparison.Ordinal);
        Assert.Contains(result.Databases, d => d.Name == "shop" && d.Ok && d.Bytes > 0);

        var sets = SetCatalog.Scan(backup, task.Id, new ArchiveAccess(_platform.Archives, null));
        string target = _temp.Combine("Restored");
        var restore = new RestoreEngine(_log).Run(
            new RestoreRequest { Sets = sets, Set = sets[0], TargetFolder = target, Existing = RestoreExisting.Overwrite, Access = new ArchiveAccess(_platform.Archives, null) },
            TestContext.Current.CancellationToken);
        Assert.Equal(RunStatuses.Success, restore.Status);
        Assert.Equal(shop, File.ReadAllText(Path.Combine(target, "db", engine, "shop.sql")));
        Assert.Contains("shop", File.ReadAllText(Path.Combine(target, "db", "README.txt")), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void A_wrong_password_makes_the_run_an_error(string engine)
    {
        var (server, account) = Server(engine);
        _platform.DbAccounts["account"] = account with { Password = "wrong" + account.Password };
        server.CredentialId = "account";
        var task = new TaskDefinition
        {
            Name = "Databases",
            BackupType = BackupTypes.Full,
            Destinations = [new TaskDestination { Kind = DestinationKinds.Local, Path = _temp.Combine("Backup") }],
            DbServers = [server],
            Vss = new VssOptions { Enabled = false },
        };

        var result = Run(task);

        Assert.Equal(RunStatuses.Error, result.Status);
        Assert.Contains(BackupEngine.DbServerLabel(server), result.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void A_missing_program_is_named(string engine)
    {
        var (server, account) = Server(engine);
        server.BinDir = _temp.Path;

        var error = Assert.Throws<DbException>(() => new ProcessDbClient(server, account).Inspect(TestContext.Current.CancellationToken));

        Assert.Contains(_temp.Path, error.Message, StringComparison.Ordinal);
    }

    private BackupResult Run(TaskDefinition task)
    {
        var engine = new BackupEngine(_platform, _store, _log);
        using var gate = new PauseGate();
        return engine.Run(new BackupRequest
        {
            Task = task,
            RunId = Guid.NewGuid().ToString(),
            Trigger = RunTriggers.Manual,
            StartedAt = Start,
            TimeZone = TimeZoneInfo.Utc,
        }, gate, TestContext.Current.CancellationToken);
    }

    private static (TaskDatabaseServer Server, DbAccount Account) Server(string engine)
    {
        string name = "CUSTOS_TEST_" + engine.ToUpperInvariant();
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(value) && Environment.GetEnvironmentVariable("CUSTOS_REQUIRE_DATABASES") == "1")
        {
            // The CI job with the servers: a lost variable must not quietly turn these tests off.
            Assert.Fail($"{name} is required here (CUSTOS_REQUIRE_DATABASES=1).");
        }

        Assert.SkipWhen(string.IsNullOrEmpty(value), $"{name} names no {DbEngines.Title(engine)} server.");
        string[] parts = value.Split('|');
        Assert.Equal(5, parts.Length);
        var server = new TaskDatabaseServer
        {
            Engine = engine,
            BinDir = parts[0],
            Host = parts[1],
            Port = int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
        };
        return (server, new DbAccount(parts[3], parts[4]));
    }
}
