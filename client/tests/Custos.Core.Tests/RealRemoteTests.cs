using System.Diagnostics;
using System.Text;
using Custos.Core.Archives;
using Custos.Core.Backup;
using Custos.Core.Model;
using Custos.Core.Remote;

namespace Custos.Core.Tests;

/// <summary>
/// The whole way against a real Linux server: custos-helper installed with the block of the wizard, OpenSSH, SSH.NET,
/// full and incremental sets, restore into tars and verification. Runs only where <c>CUSTOS_TEST_SSH</c> names the
/// server: <c>host|port|root shell|engines|db defaults file</c>, where «root shell» is a command that runs its input
/// as root on the server ("docker exec -i abc bash -s", or "bash -s" for this machine), «engines» the database servers
/// that run there ("mariadb postgresql", may be empty). The CI job «remote» starts one in Docker
/// (client/tools/ci-remote.sh). Needs tar on this machine to look into the restored tars.
/// </summary>
public sealed class RealRemoteTests : IDisposable
{
    private const string Base = "/srv/custos-test";

    private readonly TempFolder _temp = new();
    private readonly TestPlatform _platform = new();
    private readonly MemoryFileStateStore _store = new();
    private readonly TestLog _log = new();

    public void Dispose()
    {
        // The unpacked tars hold links and names that are not UTF-8, which .NET cannot delete.
        foreach (string folder in Directory.GetDirectories(_temp.Path, "untar-*"))
        {
            using var rm = Process.Start("rm", ["-rf", folder]);
            rm.WaitForExit();
        }

        _temp.Dispose();
    }

    [Fact]
    public void A_server_is_copied_in_full_and_incrementally_and_restored_into_tars()
    {
        var server = Server();
        Root(server, $$"""
            set -e
            rm -rf {{Base}}
            mkdir -p "{{Base}}/www/site one/data" {{Base}}/etc/sub
            echo 'first version' > "{{Base}}/www/site one/data/index.php"
            echo 'stays the same' > {{Base}}/www/keep.txt
            echo 'will be deleted' > {{Base}}/www/gone.txt
            echo 'Case matters' > {{Base}}/www/Logo.png
            echo 'case matters' > {{Base}}/www/logo.png
            printf 'latin1\n' > {{Base}}/www/bad$'\xff'name
            head -c 3000000 /dev/urandom > {{Base}}/www/big.bin
            ln -s keep.txt {{Base}}/www/link
            echo 'server_name example;' > {{Base}}/etc/sub/site.conf
            """);
        foreach (string engine in server.Engines)
        {
            Root(server, engine == DbEngines.PostgreSql
                ? "runuser -u postgres -- psql -q -c 'DROP DATABASE IF EXISTS site_db' -c 'CREATE DATABASE site_db'"
                : $"{(engine == DbEngines.MariaDb ? "mariadb" : "mysql")} {Defaults(server)} -e 'DROP DATABASE IF EXISTS site_db; CREATE DATABASE site_db; CREATE TABLE site_db.t (id INT); INSERT INTO site_db.t VALUES (42)'");
        }

        byte[] seed = SshKeys.NewSeed();
        _platform.SshKeySeeds["key"] = seed;
        var settings = new RemoteHelperSettings
        {
            Roots = [$"{Base}/www", $"{Base}/etc"],
            DbEngines = [.. server.Engines],
            DbDefaultsFile = server.DefaultsFile,
        };
        string block = HelperSetup.InstallBlock(settings, RemotePresets.Custom, SshKeys.PublicKeyLine(seed, "custos-test"), null);
        Assert.Contains("OK: custos-helper installed", Root(server, block), StringComparison.Ordinal);

        // The first connection shows the key of the server; it is pinned from then on.
        var unknown = Assert.Throws<SshHostKeyException>(() => SshNetConnector.Instance.Connect(
            new SshTarget(server.Host, server.Port, "custos", seed, null), TestContext.Current.CancellationToken));
        Assert.True(SshKeys.IsFingerprint(unknown.Actual), unknown.Actual);
        var task = Task(server, unknown.Actual);

        var first = Run(task, Start);
        Assert.True(first.Status == RunStatuses.Success, Log());
        Assert.Equal(BackupTypes.Full, first.BackupType);
        Assert.Equal(HelperScript.Version, first.Remote!.HelperVersion);
        Assert.All(server.Engines, e => Assert.Contains(first.Databases, d => d.Engine == e && d.Name == "site_db" && d.Ok));

        Root(server, $"""
            set -e
            echo 'second version' > "{Base}/www/site one/data/index.php"
            rm {Base}/www/gone.txt
            echo 'new file' > {Base}/www/new.txt
            """);
        var second = Run(task, Start.AddDays(1));
        Assert.True(second.Status == RunStatuses.Success, Log());
        Assert.Equal(BackupTypes.Incremental, second.BackupType);
        Assert.Equal(1, second.Remote!.FilesDeleted);

        var sets = SetCatalog.Scan(_temp.Combine("Backup"), task.Id, new ArchiveAccess(_platform.Archives, null));
        Assert.Equal(2, sets.Count);

        // The date of the second set: the new versions, the deleted file gone, everything else from the full set.
        var newest = Restore(sets, sets.Single(s => s.Header.SetName == second.SetName), "Restored");
        string www = Untar(Path.Combine(newest, "srv-custos-test-www.tar"));
        Assert.Equal("second version\n", File.ReadAllText(Path.Combine(www, "srv", "custos-test", "www", "site one", "data", "index.php")));
        Assert.Equal("new file\n", File.ReadAllText(Path.Combine(www, "srv", "custos-test", "www", "new.txt")));
        Assert.Equal("stays the same\n", File.ReadAllText(Path.Combine(www, "srv", "custos-test", "www", "keep.txt")));
        Assert.Equal("Case matters\n", File.ReadAllText(Path.Combine(www, "srv", "custos-test", "www", "Logo.png")));
        Assert.Equal("case matters\n", File.ReadAllText(Path.Combine(www, "srv", "custos-test", "www", "logo.png")));
        Assert.False(File.Exists(Path.Combine(www, "srv", "custos-test", "www", "gone.txt")));
        Assert.Equal(3000000, new FileInfo(Path.Combine(www, "srv", "custos-test", "www", "big.bin")).Length);
        Assert.Equal("keep.txt", new FileInfo(Path.Combine(www, "srv", "custos-test", "www", "link")).LinkTarget);
        Assert.Contains(Directory.GetFiles(Path.Combine(www, "srv", "custos-test", "www")), f => Path.GetFileName(f).StartsWith("bad", StringComparison.Ordinal));
        string etc = Untar(Path.Combine(newest, "srv-custos-test-etc.tar"));
        Assert.Equal("server_name example;\n", File.ReadAllText(Path.Combine(etc, "srv", "custos-test", "etc", "sub", "site.conf")));
        string readme = File.ReadAllText(Path.Combine(newest, "README.txt"));
        Assert.Contains("tar -xpf srv-custos-test-www.tar -C /", readme, StringComparison.Ordinal);
        Assert.Contains("tar -xpf srv-custos-test-etc.tar -C /root/old-etc", readme, StringComparison.Ordinal);
        foreach (string engine in server.Engines)
        {
            string dump = File.ReadAllText(Path.Combine(newest, "db", engine, "site_db.sql"));
            Assert.True(Databases.DumpCheck.HasEndLine(engine, Encoding.UTF8.GetBytes(dump)), dump);
            Assert.Contains($"db/{engine}/site_db.sql", readme, StringComparison.Ordinal);
        }

        // The date of the full set: the old version and the file deleted later.
        var oldest = Restore(sets, sets.Single(s => s.Header.SetName == first.SetName), "RestoredFirst");
        string oldWww = Untar(Path.Combine(oldest, "srv-custos-test-www.tar"));
        Assert.Equal("first version\n", File.ReadAllText(Path.Combine(oldWww, "srv", "custos-test", "www", "site one", "data", "index.php")));
        Assert.True(File.Exists(Path.Combine(oldWww, "srv", "custos-test", "www", "gone.txt")));
        Assert.False(File.Exists(Path.Combine(oldWww, "srv", "custos-test", "www", "new.txt")));
    }

    [Fact]
    public void A_changed_key_of_the_server_stops_the_run_before_anything_is_read()
    {
        var server = Server();
        _platform.SshKeySeeds["key"] = SshKeys.NewSeed();
        var task = Task(server, "SHA256:" + new string('A', 43));

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Error, result.Status);
        Assert.Contains("SHA256:AAAA", result.ErrorMessage, StringComparison.Ordinal);
    }

    private static readonly DateTimeOffset Start = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    private TaskDefinition Task(TestServer server, string hostKey) => new()
    {
        Name = "Linux server",
        BackupType = BackupTypes.Incremental,
        Sources =
        [
            new TaskSource
            {
                Kind = SourceKinds.Ssh,
                Host = server.Host,
                Port = server.Port,
                User = "custos",
                CredentialId = "key",
                HostKeySha256 = hostKey,
                Preset = RemotePresets.Custom,
            },
        ],
        Archive = new ArchiveOptions { Format = ArchiveFormats.Zip, Level = 1 },
        Destinations = [new TaskDestination { Kind = DestinationKinds.Local, Path = _temp.Combine("Backup") }],
        Verify = new VerifyOptions { EveryRuns = 1 },
        Vss = new VssOptions { Enabled = false },
    };

    private BackupResult Run(TaskDefinition task, DateTimeOffset when)
    {
        var engine = new BackupEngine(_platform, _store, _log);
        using var gate = new PauseGate();
        return engine.Run(new BackupRequest
        {
            Task = task,
            RunId = Guid.NewGuid().ToString(),
            Trigger = RunTriggers.Manual,
            StartedAt = when,
            TimeZone = TimeZoneInfo.Utc,
            RemoteRetryDelay = TimeSpan.Zero,
        }, gate, TestContext.Current.CancellationToken);
    }

    private string Restore(IReadOnlyList<StoredSet> sets, StoredSet set, string name)
    {
        string target = _temp.Combine(name);
        var result = new RestoreEngine(_log).Run(
            new RestoreRequest { Sets = sets, Set = set, TargetFolder = target, Existing = RestoreExisting.Overwrite, Access = new ArchiveAccess(_platform.Archives, null) },
            TestContext.Current.CancellationToken);
        Assert.True(result.Status == RunStatuses.Success, Log());
        return target;
    }

    /// <summary>Unpacks a restored tar with the tar of this machine, into a new folder.</summary>
    private string Untar(string tar)
    {
        string folder = _temp.Combine("untar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var start = new ProcessStartInfo("tar") { RedirectStandardError = true };
        foreach (string word in new[] { "-xf", tar, "-C", folder })
        {
            start.ArgumentList.Add(word);
        }

        using var process = Process.Start(start)!;
        string errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, errors);
        return folder;
    }

    private string Log() => string.Join(Environment.NewLine, _log.Lines.Select(l => l.Message));

    private static string Defaults(TestServer server) => server.DefaultsFile is { } file ? "--defaults-extra-file=" + file : string.Empty;

    /// <summary>Runs a script as root on the server; gives its output.</summary>
    private static string Root(TestServer server, string script)
    {
        var start = new ProcessStartInfo("sh", ["-c", server.RootShell])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        process.StandardInput.Write(script.ReplaceLineEndings("\n") + "\n");
        process.StandardInput.Close();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output + errors.Result);
        return output;
    }

    private static TestServer Server()
    {
        string? value = Environment.GetEnvironmentVariable("CUSTOS_TEST_SSH");
        if (string.IsNullOrEmpty(value) && Environment.GetEnvironmentVariable("CUSTOS_REQUIRE_SSH") == "1")
        {
            Assert.Fail("CUSTOS_TEST_SSH is required here (CUSTOS_REQUIRE_SSH=1).");
        }

        Assert.SkipWhen(string.IsNullOrEmpty(value), "No Linux server: CUSTOS_TEST_SSH is not set.");
        string[] parts = value!.Split('|');
        return new TestServer(parts[0], int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), parts[2],
            parts.Length > 3 ? parts[3].Split(' ', StringSplitOptions.RemoveEmptyEntries) : [],
            parts.Length > 4 && parts[4].Length > 0 ? parts[4] : null);
    }

    private sealed record TestServer(string Host, int Port, string RootShell, string[] Engines, string? DefaultsFile);
}
