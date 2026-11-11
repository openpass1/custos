using System.Formats.Tar;
using System.Text;
using Custos.Core.Archives;
using Custos.Core.Backup;
using Custos.Core.Model;
using Custos.Core.Remote;

namespace Custos.Core.Tests;

/// <summary>Tasks with a Linux server as source (CUSTOS-remote-pull.md 4.3–4.5), against <see cref="FakeLinuxServer"/>.</summary>
public sealed class RemoteBackupTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    private readonly TempFolder _temp = new();
    private readonly TestPlatform _platform = new();
    private readonly MemoryFileStateStore _store = new();
    private readonly TestLog _log = new();
    private readonly FakeLinuxServer _server = new();

    public RemoteBackupTests()
    {
        _platform.SshOverride = _server;
        _platform.SshKeySeeds["key"] = SshKeys.NewSeed();
        long dayAgo = _server.Now - 86400;
        _server.AddFile("/var/www/site/index.php", "v1", dayAgo);
        _server.AddFile("/var/www/site/Logo.png", "big L", dayAgo);
        _server.AddFile("/var/www/site/logo.png", "small l", dayAgo);
        _server.AddFile("/var/www/old.txt", "old", dayAgo);
        _server.AddLink("/var/www/current", "site", dayAgo);
        _server.AddFile("/etc/nginx/nginx.conf", "worker_processes 1;", dayAgo);
        _server.Databases[DbEngines.MariaDb] = ["site_db"];
    }

    public void Dispose() => _temp.Dispose();

    public static TheoryData<string> Formats => [ArchiveFormats.Zip, ArchiveFormats.None];

    [Theory]
    [MemberData(nameof(Formats))]
    public void Full_then_incremental_copies_restore_every_date_into_tars(string format)
    {
        var task = Task(format);
        var first = Run(task, Start);
        Assert.True(first.Status == RunStatuses.Success, Log());
        Assert.Equal(BackupTypes.Full, first.BackupType);
        Assert.Contains(_server.Commands, c => c == "tar-since 0 gzip");
        Assert.Contains(first.Databases, d => d.Name == "site_db" && d.Ok);
        Assert.Equal(6, first.Remote!.FilesChanged);

        // An hour later on the server: one file changed, one deleted, one new.
        _server.Now += 3600;
        _server.AddFile("/var/www/site/index.php", "v2");
        _server.Delete("/var/www/old.txt");
        _server.AddFile("/var/www/site/new.txt", "new");
        _server.Commands.Clear();
        var second = Run(task, Start.AddDays(1));
        Assert.True(second.Status == RunStatuses.Success, Log());
        Assert.Equal(BackupTypes.Incremental, second.BackupType);
        Assert.Contains(_server.Commands, c => c == $"tar-since {_server.Now - 3600 - BackupEngine.RemoteOverlapSeconds} gzip");
        Assert.Equal(2, second.Remote!.FilesChanged);
        Assert.Equal(1, second.Remote.FilesDeleted);

        var sets = Sets(task);
        var latest = Restore(sets, second.SetName!);
        var www = ReadTar(Path.Combine(latest, "var-www.tar"));
        Assert.Equal("v2", www["var/www/site/index.php"]);
        Assert.Equal("new", www["var/www/site/new.txt"]);
        Assert.Equal("big L", www["var/www/site/Logo.png"]);
        Assert.Equal("small l", www["var/www/site/logo.png"]);
        Assert.Equal("-> site", www["var/www/current"]);
        Assert.False(www.ContainsKey("var/www/old.txt"));
        Assert.Equal("worker_processes 1;", ReadTar(Path.Combine(latest, "etc-nginx.tar"))["etc/nginx/nginx.conf"]);
        Assert.StartsWith("-- dump of mariadb site_db", File.ReadAllText(Path.Combine(latest, "db", "mariadb", "site_db.sql")), StringComparison.Ordinal);
        string readme = File.ReadAllText(Path.Combine(latest, "README.txt"));
        Assert.Contains("tar -xpf var-www.tar -C /", readme, StringComparison.Ordinal);
        Assert.Contains("mkdir -p /root/old-etc && tar -xpf etc-nginx.tar -C /root/old-etc", readme, StringComparison.Ordinal);
        Assert.Contains("mariadb site_db < db/mariadb/site_db.sql", readme, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', readme);

        var full = ReadTar(Path.Combine(Restore(sets, first.SetName!), "var-www.tar"));
        Assert.Equal("v1", full["var/www/site/index.php"]);
        Assert.Equal("old", full["var/www/old.txt"]);
        Assert.False(full.ContainsKey("var/www/site/new.txt"));
    }

    [Fact]
    public void A_7z_archive_lists_the_files_before_the_tar_and_restore_still_takes_them_from_the_tar()
    {
        // ZIP that packs by name as 7-Zip does: custos-set.json comes before db/ and files.tar.
        _platform.ArchivesOverride = new NameSortingArchives(_platform.Archives);
        var task = Task(ArchiveFormats.Zip);
        Assert.Equal(RunStatuses.Success, Run(task, Start).Status);
        _server.Now += 3600;
        _server.AddFile("/var/www/site/index.php", "v2");
        var second = Run(task, Start.AddDays(1));
        Assert.True(second.Status == RunStatuses.Success, Log());

        var www = ReadTar(Path.Combine(Restore(Sets(task), second.SetName!), "var-www.tar"));
        Assert.Equal("v2", www["var/www/site/index.php"]);
        Assert.Equal("old", www["var/www/old.txt"]);
    }

    [Fact]
    public void A_differential_copy_takes_what_changed_since_the_full_one()
    {
        var task = Task(ArchiveFormats.Zip);
        task.BackupType = BackupTypes.Differential;
        Assert.Equal(RunStatuses.Success, Run(task, Start).Status);
        long fullNow = _server.Now;
        _server.Now += 3600;
        Assert.Equal(RunStatuses.Success, Run(task, Start.AddDays(1)).Status);
        _server.Now += 3600;
        _server.Commands.Clear();

        Assert.Equal(RunStatuses.Success, Run(task, Start.AddDays(2)).Status);

        Assert.Contains(_server.Commands, c => c == $"tar-since {fullNow - BackupEngine.RemoteOverlapSeconds} gzip");
    }

    [Fact]
    public void A_changed_key_of_the_server_stops_the_run_and_nothing_is_read()
    {
        var task = Task(ArchiveFormats.Zip);
        _server.HostKey = "SHA256:" + new string('B', 43);

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Error, result.Status);
        Assert.Empty(_server.Commands);
        Assert.Single(_server.PinnedKeys);
        Assert.Empty(Sets(task));
    }

    [Fact]
    public void A_server_out_of_reach_is_tried_three_times()
    {
        var task = Task(ArchiveFormats.Zip);
        _server.Unreachable = 2;
        Assert.True(Run(task, Start).Status == RunStatuses.Success, Log());
        Assert.Equal(3, _server.PinnedKeys.Count);

        _server.Unreachable = 3;
        var result = Run(task, Start.AddDays(1));

        Assert.Equal(RunStatuses.Error, result.Status);
        Assert.Contains("3", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void A_task_without_a_pinned_key_does_not_connect()
    {
        var task = Task(ArchiveFormats.Zip);
        task.Sources[0].HostKeySha256 = null;

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Error, result.Status);
        Assert.Empty(_server.PinnedKeys);
    }

    [Fact]
    public void An_incompatible_custos_helper_is_an_error_and_an_older_one_a_warning()
    {
        var task = Task(ArchiveFormats.Zip);
        _server.Protocol = 2;
        Assert.Equal(RunStatuses.Error, Run(task, Start).Status);

        _server.Protocol = HelperScript.Protocol;
        _server.HelperVersion = "0.9.0";
        Assert.Equal(RunStatuses.Warning, Run(task, Start.AddDays(1)).Status);
    }

    [Fact]
    public void A_failed_dump_makes_the_run_an_error_but_the_set_is_kept_and_the_next_run_starts_from_the_last_good_one()
    {
        var task = Task(ArchiveFormats.Zip);
        Assert.Equal(RunStatuses.Success, Run(task, Start).Status);
        long goodNow = _server.Now;
        _server.Now += 3600;
        _server.BrokenDumps.Add("site_db");
        var broken = Run(task, Start.AddDays(1));
        Assert.Equal(RunStatuses.Error, broken.Status);
        Assert.Contains(broken.Databases, d => d.Name == "site_db" && !d.Ok);
        Assert.Equal(2, Sets(task).Count);

        _server.BrokenDumps.Clear();
        _server.Now += 3600;
        _server.Commands.Clear();
        Assert.Equal(RunStatuses.Success, Run(task, Start.AddDays(2)).Status);
        Assert.Contains(_server.Commands, c => c == $"tar-since {goodNow - BackupEngine.RemoteOverlapSeconds} gzip");
    }

    [Fact]
    public void The_databases_ticked_in_the_wizard_are_dumped_and_the_rest_is_named_in_the_log()
    {
        _server.Databases[DbEngines.MariaDb] = ["site_db", "shop_db", "old_db"];
        var task = Task(ArchiveFormats.Zip);

        // «All databases» with one unticked: the others are dumped, new ones too.
        task.Sources[0].Databases = new RemoteDatabaseChoice { Exclude = ["mariadb/old_db"] };
        var all = Run(task, Start);
        Assert.Equal(RunStatuses.Success, all.Status);
        Assert.Equal(["site_db", "shop_db"], all.Databases.Select(d => d.Name));
        Assert.Contains(Messages.Format(Messages.RemoteDbNotChosen, "MariaDB vps.example.org", "old_db"), Log(), StringComparison.Ordinal);
        Assert.DoesNotContain(_server.Commands, c => c.StartsWith("db-dump mariadb old_db", StringComparison.Ordinal));

        // «Selected databases»: only the ticked ones; a database made since is named, not dumped.
        _server.Databases[DbEngines.MariaDb].Add("new_db");
        task.Sources[0].Databases = new RemoteDatabaseChoice { AllDatabases = false, Include = ["mariadb/shop_db"] };
        _server.Now += 3600;
        var only = Run(task, Start.AddDays(1));
        Assert.Equal(RunStatuses.Success, only.Status);
        Assert.Equal(["shop_db"], only.Databases.Select(d => d.Name));
        Assert.Contains(Messages.Format(Messages.RemoteDbNotChosen, "MariaDB vps.example.org", "site_db, old_db, new_db"), Log(), StringComparison.Ordinal);

        // A ticked database that is gone from the server is an error, like on Windows.
        task.Sources[0].Databases!.Include.Add("mariadb/gone_db");
        _server.Now += 3600;
        var gone = Run(task, Start.AddDays(2));
        Assert.Equal(RunStatuses.Error, gone.Status);
        Assert.Contains(gone.Databases, d => d.Name == "gone_db" && !d.Ok);
        Assert.Contains(gone.Databases, d => d.Name == "shop_db" && d.Ok);
    }

    [Fact]
    public void Files_that_change_while_read_make_a_warning()
    {
        _server.TarExit = (1, "tar: /var/www/site/index.php: file changed as we read it");

        var result = Run(Task(ArchiveFormats.Zip), Start);

        Assert.Equal(RunStatuses.Warning, result.Status);
        Assert.Contains(_log.Lines, l => l.Message.Contains("file changed as we read it", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void A_tar_that_breaks_off_makes_no_set(string format)
    {
        _server.TarCutAfter = 1536;

        var result = Run(Task(format), Start);

        Assert.Equal(RunStatuses.Error, result.Status);
        Assert.Contains("connection lost", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(Sets(Task(format)));
    }

    [Fact]
    public void The_memory_guard_stops_a_server_that_sends_faster_than_the_archive_takes_it()
    {
        _server.TarBuffered = HelperClient.DefaultMemoryLimit + 1;

        var result = Run(Task(ArchiveFormats.Zip), Start);

        Assert.Equal(RunStatuses.Error, result.Status);
        Assert.Contains(Messages.Format(Messages.RemoteMemoryGuard, "").Split(',')[0], Log(), StringComparison.Ordinal);
    }

    [Fact]
    public void Verification_of_stored_sets_finds_a_damaged_file_inside_the_tar()
    {
        var task = Task(ArchiveFormats.None);
        task.Verify = new VerifyOptions { StoredEveryDays = 1 };
        Assert.Equal(RunStatuses.Success, Run(task, Start).Status);
        string tar = Path.Combine(Sets(task)[0].Location, BackupEngine.RemoteTarName);
        byte[] bytes = File.ReadAllBytes(tar);
        int at = IndexOf(bytes, Encoding.UTF8.GetBytes("worker_processes"));
        bytes[at] = (byte)'W';
        File.WriteAllBytes(tar, bytes);

        var result = Run(task, Start.AddDays(2));

        Assert.Equal(RunStatuses.Error, result.Status);
        Assert.Contains("/etc/nginx/nginx.conf", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_needs_a_folder_and_marks_damaged_files_as_failed()
    {
        var task = Task(ArchiveFormats.None);
        Assert.Equal(RunStatuses.Success, Run(task, Start).Status);
        var sets = Sets(task);
        var access = new ArchiveAccess(_platform.Archives, null);
        var toOriginal = new RestoreEngine(_log).Run(new RestoreRequest { Sets = sets, Set = sets[0], Access = access }, TestContext.Current.CancellationToken);
        Assert.Equal(RunStatuses.Error, toOriginal.Status);
        Assert.Equal(Messages.RestoreRemoteNeedsFolder, toOriginal.ErrorMessage);

        string tar = Path.Combine(sets[0].Location, BackupEngine.RemoteTarName);
        byte[] bytes = File.ReadAllBytes(tar);
        bytes[IndexOf(bytes, Encoding.UTF8.GetBytes("small l"))] = (byte)'S';
        File.WriteAllBytes(tar, bytes);
        string target = _temp.Combine("Restored");
        var result = new RestoreEngine(_log).Run(
            new RestoreRequest { Sets = sets, Set = sets[0], TargetFolder = target, Existing = RestoreExisting.Overwrite, Access = access },
            TestContext.Current.CancellationToken);

        Assert.Equal(RunStatuses.Error, result.Status);
        Assert.Equal(1, result.FilesFailed);
        var www = ReadTar(Path.Combine(target, "var-www.tar"));
        Assert.False(www.ContainsKey("var/www/site/logo.png"));
        Assert.Equal("big L", www["var/www/site/Logo.png"]);
    }

    private TaskDefinition Task(string format) => new()
    {
        Id = "11111111-2222-3333-4444-555555555555",
        Name = "Server",
        BackupType = BackupTypes.Incremental,
        Sources =
        [
            new TaskSource
            {
                Kind = SourceKinds.Ssh,
                Host = "vps.example.org",
                Port = 22,
                User = "custos",
                CredentialId = "key",
                HostKeySha256 = "SHA256:" + new string('A', 43),
                Preset = RemotePresets.IspmanagerSites,
            },
        ],
        Archive = new ArchiveOptions { Format = format, Level = 1 },
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

    private List<StoredSet> Sets(TaskDefinition task)
    {
        string folder = _temp.Combine("Backup");
        return Directory.Exists(folder) ? [.. SetCatalog.Scan(folder, task.Id, new ArchiveAccess(_platform.Archives, null))] : [];
    }

    private string Restore(List<StoredSet> sets, string setName)
    {
        string target = _temp.Combine("Restored-" + Guid.NewGuid().ToString("N"));
        var result = new RestoreEngine(_log).Run(
            new RestoreRequest { Sets = sets, Set = sets.Single(s => s.Header.SetName == setName), TargetFolder = target, Existing = RestoreExisting.Overwrite, Access = new ArchiveAccess(_platform.Archives, null) },
            TestContext.Current.CancellationToken);
        Assert.True(result.Status == RunStatuses.Success, Log());
        return target;
    }

    /// <summary>The members of a tar: name → content (folders "", links "-> target").</summary>
    private static Dictionary<string, string> ReadTar(string path)
    {
        var members = new Dictionary<string, string>(StringComparer.Ordinal);
        using var stream = File.OpenRead(path);
        using var reader = new TarReader(stream);
        while (reader.GetNextEntry() is { } entry)
        {
            string content = entry.EntryType switch
            {
                TarEntryType.Directory => string.Empty,
                TarEntryType.SymbolicLink => "-> " + entry.LinkName,
                _ => entry.DataStream is null ? string.Empty : new StreamReader(entry.DataStream).ReadToEnd(),
            };
            Assert.False(members.ContainsKey(entry.Name.TrimEnd('/')), "twice in the tar: " + entry.Name);
            members[entry.Name.TrimEnd('/')] = content;
        }

        return members;
    }

    private static int IndexOf(byte[] bytes, byte[] part)
    {
        for (int i = 0; i + part.Length <= bytes.Length; i++)
        {
            if (bytes.AsSpan(i, part.Length).SequenceEqual(part))
            {
                return i;
            }
        }

        throw new InvalidOperationException("not found");
    }

    private string Log() => string.Join(Environment.NewLine, _log.Lines.Select(l => l.Message));
}
