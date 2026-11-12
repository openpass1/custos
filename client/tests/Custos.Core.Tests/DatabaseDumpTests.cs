using System.Security.Cryptography;
using System.Text;
using Custos.Core.Archives;
using Custos.Core.Backup;
using Custos.Core.Databases;
using Custos.Core.Model;

namespace Custos.Core.Tests;

/// <summary>Dumps of databases in a backup run and their restore (section 6.2), with servers played by <see cref="FakeDbClient"/>.</summary>
public sealed class DatabaseDumpTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Modified = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly TempFolder _temp = new();
    private readonly TestPlatform _platform = new();
    private readonly MemoryFileStateStore _store = new();
    private readonly TestLog _log = new();
    private readonly string _source;
    private readonly string _backup;
    private readonly FakeDbClient _mysql = new(DbEngines.MySql);
    private readonly FakeDbClient _postgres = new(DbEngines.PostgreSql) { Version = "17.6" };

    public DatabaseDumpTests()
    {
        _source = _temp.Combine("Data");
        _backup = _temp.Combine("Backup");
        _temp.WriteFile("Data/a.txt", "alpha", Modified);
        _mysql.Databases.AddRange(["information_schema", "mysql", "performance_schema", "sys", "shop", "blog", "old site"]);
        _postgres.Databases.AddRange(["postgres", "crm"]);
        _platform.DbClients = (server, _) => server.Engine == DbEngines.PostgreSql ? _postgres : _mysql;
        _platform.DbAccounts["root-account"] = new DbAccount("root", "secret");
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Dumps_go_into_db_of_a_plain_set_with_the_users_and_the_manifest_lists_them()
    {
        var task = NewTask(ArchiveFormats.None);
        task.DbServers[0].Exclude = ["blog"];

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Success, result.Status);
        string set = Path.Combine(_backup, result.SetName!);
        Assert.Equal(_mysql.Sql("shop"), Read(set, "db", "mysql", "shop.sql"));
        Assert.Equal(_mysql.Sql("old site"), Read(set, "db", "mysql", "old_site.sql"));
        Assert.Equal(_mysql.Sql("_users"), Read(set, "db", "mysql", "_users.sql"));
        Assert.False(File.Exists(Path.Combine(set, "db", "mysql", "blog.sql")));
        Assert.Equal("alpha", Read(set, "Data", "a.txt"));
        Assert.Equal(["shop", "old site", null], _mysql.Dumped);

        var header = ReadHeader(set);
        Assert.Equal(3, header.Databases.Count);
        var shop = header.Databases.Single(d => d.Name == "shop");
        Assert.Equal("db/mysql/shop.sql", shop.File);
        Assert.Equal("127.0.0.1:3306", shop.Server);
        Assert.True(shop.Ok);
        Assert.Equal(Sha256(_mysql.Sql("shop")), shop.Sha256);
        Assert.Equal(Encoding.UTF8.GetByteCount(_mysql.Sql("shop")), shop.Bytes);
        Assert.True(header.Databases.Single(d => d.IsUsers).Ok);

        Assert.Equal(2, result.Databases.Count);
        Assert.All(result.Databases, d => Assert.True(d.Ok));
        Assert.Equal(4, result.SourceFiles);
        Assert.Contains(_log.Lines, l => l.Message.Contains("shop", StringComparison.Ordinal) && l.Message.Contains("old site", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_the_chosen_databases_are_dumped_and_a_missing_one_is_an_error()
    {
        var task = NewTask(ArchiveFormats.None);
        task.DbServers[0].AllDatabases = false;
        task.DbServers[0].Include = ["SHOP", "gone"];

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Error, result.Status);
        Assert.Equal(["shop", null], _mysql.Dumped);
        var gone = result.Databases.Single(d => d.Name == "gone");
        Assert.False(gone.Ok);
        Assert.Contains("gone", gone.Error, StringComparison.Ordinal);
        string set = Path.Combine(_backup, result.SetName!);
        Assert.True(File.Exists(Path.Combine(set, "db", "mysql", "shop.sql")));
        var header = ReadHeader(set);
        Assert.Equal(RunStatuses.Error, header.Status);
        Assert.Null(header.Databases.Single(d => d.Name == "gone").File);
    }

    [Fact]
    public void A_broken_dump_is_an_error_but_the_set_is_kept_without_it()
    {
        var task = NewTask(ArchiveFormats.None);
        _mysql.Broken.Add("blog");

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Error, result.Status);
        string set = Path.Combine(_backup, result.SetName!);
        Assert.True(File.Exists(Path.Combine(set, "db", "mysql", "shop.sql")));
        Assert.False(File.Exists(Path.Combine(set, "db", "mysql", "blog.sql")));
        Assert.Empty(Directory.GetFiles(Path.Combine(set, "db", "mysql"), "*.partial"));
        Assert.Equal("alpha", Read(set, "Data", "a.txt"));
        var blog = ReadHeader(set).Databases.Single(d => d.Name == "blog");
        Assert.False(blog.Ok);
        Assert.Null(blog.File);
        Assert.Contains("2", blog.Error, StringComparison.Ordinal);
        Assert.Contains(_log.Lines, l => l.Message == Messages.DbSetKeptNotCommitted);

        // Not committed: the next incremental run is a full one again.
        task.BackupType = BackupTypes.Incremental;
        _mysql.Broken.Clear();
        var next = Run(task, Start.AddDays(1));
        Assert.Equal(RunStatuses.Success, next.Status);
        Assert.Equal(BackupTypes.Full, next.BackupType);
    }

    [Fact]
    public void A_dump_without_its_end_line_fails_even_with_exit_code_zero()
    {
        var fake = new FakeDbClient(DbEngines.MySql);
        fake.Databases.Add("shop");
        var check = DumpCheck.Problem(DbEngines.MySql, Encoding.UTF8.GetBytes(fake.Sql("shop", complete: false)), new DbDumpExit(0, string.Empty));
        Assert.Equal(Messages.DumpNoEndLine, check);
        Assert.Null(DumpCheck.Problem(DbEngines.MySql, Encoding.UTF8.GetBytes(fake.Sql("shop")), new DbDumpExit(0, string.Empty)));
        Assert.NotNull(DumpCheck.Problem(DbEngines.MySql, Encoding.UTF8.GetBytes(fake.Sql("shop")), new DbDumpExit(1, "oops")));
    }

    [Fact]
    public void An_unreachable_server_is_an_error_and_the_files_are_still_copied()
    {
        var task = NewTask(ArchiveFormats.None);
        _mysql.Unavailable = new DbException("Can't connect to MySQL server on '127.0.0.1:3306'");

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Error, result.Status);
        Assert.Contains("127.0.0.1:3306", result.ErrorMessage, StringComparison.Ordinal);
        string set = Path.Combine(_backup, result.SetName!);
        Assert.Equal("alpha", Read(set, "Data", "a.txt"));
        Assert.False(Directory.Exists(Path.Combine(set, "db")));
        Assert.Empty(_mysql.Dumped);
    }

    [Fact]
    public void A_task_with_only_databases_needs_no_sources()
    {
        var task = NewTask(ArchiveFormats.None);
        task.Sources.Clear();
        task.BackupType = BackupTypes.Incremental;

        var first = Run(task, Start);
        var second = Run(task, Start.AddDays(1));

        Assert.Equal(RunStatuses.Success, first.Status);
        Assert.Equal(RunStatuses.Success, second.Status);
        Assert.False(second.NothingToDo);
        Assert.True(File.Exists(Path.Combine(_backup, second.SetName!, "db", "mysql", "shop.sql")));
    }

    [Fact]
    public void Two_servers_of_one_engine_get_folders_of_their_own()
    {
        var task = NewTask(ArchiveFormats.None);
        task.DbServers.Add(new TaskDatabaseServer { Engine = DbEngines.MySql, BinDir = _temp.Path, Host = "127.127.126.5", Port = 3307 });
        task.DbServers.Add(new TaskDatabaseServer { Engine = DbEngines.PostgreSql, BinDir = _temp.Path, CredentialId = "root-account" });

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Success, result.Status);
        string set = Path.Combine(_backup, result.SetName!);
        Assert.True(File.Exists(Path.Combine(set, "db", "mysql", "shop.sql")));
        Assert.True(File.Exists(Path.Combine(set, "db", "mysql-2", "shop.sql")));
        Assert.True(File.Exists(Path.Combine(set, "db", "postgresql", "crm.sql")));
        Assert.True(File.Exists(Path.Combine(set, "db", "postgresql", "postgres.sql")));
        Assert.True(File.Exists(Path.Combine(set, "db", "postgresql", "_users.sql")));
        Assert.Contains(ReadHeader(set).Databases, d => d.Server == "127.127.126.5:3307");
    }

    [Fact]
    public void A_source_folder_named_db_moves_aside()
    {
        _temp.WriteFile("Other/db/x.txt", "x", Modified);
        var task = NewTask(ArchiveFormats.None);
        task.Sources.Add(new TaskSource { Path = _temp.Combine("Other", "db") });

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Success, result.Status);
        string set = Path.Combine(_backup, result.SetName!);
        Assert.Equal("x", Read(set, "db (2)", "x.txt"));
        Assert.True(File.Exists(Path.Combine(set, "db", "mysql", "shop.sql")));
    }

    [Fact]
    public void Dumps_in_a_zip_are_checked_and_listed_with_their_checksums()
    {
        var task = NewTask(ArchiveFormats.Zip);

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Success, result.Status);
        var set = Assert.Single(SetCatalog.Scan(_backup, task.Id, Access()));
        var files = Unpack(set);
        Assert.Equal(_mysql.Sql("shop"), files[@"db\mysql\shop.sql"]);
        Assert.Equal(_mysql.Sql("_users"), files[@"db\mysql\_users.sql"]);
        var shop = set.Header.Databases.Single(d => d.Name == "shop");
        Assert.True(shop.Ok);
        Assert.Equal(Sha256(_mysql.Sql("shop")), shop.Sha256);
    }

    [Fact]
    public void A_dump_that_breaks_inside_an_archive_ends_with_the_failure_line()
    {
        _platform.ArchivesOverride = new NameSortingArchives(_platform.Archives);
        var task = NewTask(ArchiveFormats.Zip);
        _mysql.Broken.Add("blog");
        _mysql.Refused.Add("shop");

        var result = Run(task, Start);

        Assert.Equal(RunStatuses.Error, result.Status);
        var set = Assert.Single(SetCatalog.Scan(_backup, task.Id, Access()));
        var files = Unpack(set);
        string blog = files[@"db\mysql\blog.sql"];
        Assert.StartsWith(_mysql.Sql("blog", complete: false), blog, StringComparison.Ordinal);
        Assert.Contains(DumpCheck.IncompleteMarker, blog, StringComparison.Ordinal);
        Assert.False(DumpCheck.HasEndLine(DbEngines.MySql, Encoding.UTF8.GetBytes(blog)));
        Assert.False(files.ContainsKey(@"db\mysql\shop.sql"));

        // As with 7-Zip, the manifest was packed before the dumps: it lists them without a result (the run history
        // has it), and the failure line marks the broken dump inside.
        Assert.All(set.Header.Databases, d => Assert.Null(d.Ok));
        Assert.False(result.Databases.Single(d => d.Name == "blog").Ok);
        Assert.False(result.Databases.Single(d => d.Name == "shop").Ok);
        Assert.True(result.Databases.Single(d => d.Name == "old site").Ok);
    }

    [Fact]
    public void Restore_puts_the_dumps_into_db_with_a_readme()
    {
        var task = NewTask(ArchiveFormats.None);
        task.DbServers.Add(new TaskDatabaseServer { Engine = DbEngines.PostgreSql, BinDir = _temp.Path, Host = "127.127.126.7" });
        Run(task, Start);
        var sets = SetCatalog.Scan(_backup, task.Id, Access());
        string target = _temp.Combine("Restored");

        var result = Restore(sets, sets[0], target, dumpsFolder: null);

        Assert.Equal(RunStatuses.Success, result.Status);
        Assert.Equal("alpha", Read(target, "Data", "a.txt"));
        Assert.Equal(_mysql.Sql("shop"), Read(target, "db", "mysql", "shop.sql"));
        Assert.Equal(_postgres.Sql("crm"), Read(target, "db", "postgresql", "crm.sql"));
        string readme = Read(target, "db", "README.txt");
        Assert.Contains(@"mysql -h 127.0.0.1 -P 3306 -u root -p shop < db\mysql\shop.sql", readme, StringComparison.Ordinal);
        Assert.Contains("CREATE DATABASE IF NOT EXISTS `old site`", readme, StringComparison.Ordinal);
        Assert.Contains(@"mysql -h 127.0.0.1 -P 3306 -u root -p ""old site"" < db\mysql\old_site.sql", readme, StringComparison.Ordinal);
        Assert.Contains(@"createdb -h 127.127.126.7 -p 5432 -U postgres crm", readme, StringComparison.Ordinal);
        Assert.Contains(@"psql -h 127.127.126.7 -p 5432 -U postgres -d crm -f db\postgresql\crm.sql", readme, StringComparison.Ordinal);
        Assert.Contains(@"psql -h 127.127.126.7 -p 5432 -U postgres -d postgres -f db\postgresql\_users.sql", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_to_the_original_places_needs_a_folder_for_the_dumps()
    {
        var task = NewTask(ArchiveFormats.None);
        Run(task, Start);
        var sets = SetCatalog.Scan(_backup, task.Id, Access());
        File.Delete(Path.Combine(_source, "a.txt"));

        var without = Restore(sets, sets[0], target: null, dumpsFolder: null);
        Assert.Equal(RunStatuses.Warning, without.Status);
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(_source, "a.txt")));
        Assert.Contains(_log.Lines, l => l.Message == Messages.RestoreDumpsNoFolder);

        string dumps = _temp.Combine("Dumps");
        var with = Restore(sets, sets[0], target: null, dumpsFolder: dumps);
        Assert.Equal(RunStatuses.Success, with.Status);
        Assert.Equal(_mysql.Sql("blog"), Read(dumps, "db", "mysql", "blog.sql"));
        Assert.True(File.Exists(Path.Combine(dumps, "db", "README.txt")));
    }

    [Fact]
    public void An_incomplete_dump_is_restored_under_a_name_that_says_so()
    {
        _platform.ArchivesOverride = new NameSortingArchives(_platform.Archives);
        var task = NewTask(ArchiveFormats.Zip);
        _mysql.Broken.Add("blog");
        _mysql.Unavailable = null;
        Run(task, Start);
        var sets = SetCatalog.Scan(_backup, task.Id, Access());
        string target = _temp.Combine("Restored");

        var result = Restore(sets, sets[0], target, dumpsFolder: null);

        Assert.Equal(RunStatuses.Warning, result.Status);
        Assert.False(File.Exists(Path.Combine(target, "db", "mysql", "blog.sql")));
        Assert.Contains(DumpCheck.IncompleteMarker, Read(target, "db", "mysql", "blog.incomplete.sql"), StringComparison.Ordinal);
        Assert.Equal(_mysql.Sql("shop"), Read(target, "db", "mysql", "shop.sql"));
        string readme = Read(target, "db", "README.txt");
        Assert.Contains(@"db\mysql\blog.incomplete.sql", readme, StringComparison.Ordinal);
        Assert.DoesNotContain(@"< db\mysql\blog", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void The_manifest_keeps_the_databases()
    {
        var header = new SetManifestHeader
        {
            RunId = "r",
            TaskId = "t",
            TaskName = "Docs",
            SetName = "Docs 2026-11-01",
            BackupType = BackupTypes.Full,
            StartedAt = Start,
            Computer = "TESTPC",
            Status = RunStatuses.Success,
            Databases =
            [
                new ManifestDatabase { Engine = DbEngines.MariaDb, Name = "shop", File = "db/mariadb/shop.sql", Server = "127.0.0.1:3306", Bytes = 10, Sha256 = "ab", Ok = true },
                new ManifestDatabase { Engine = DbEngines.MariaDb, Name = null, File = "db/mariadb/_users.sql", Server = "127.0.0.1:3306" },
                new ManifestDatabase { Engine = DbEngines.MariaDb, Name = "gone", Server = "127.0.0.1:3306", Ok = false, Error = "not there" },
            ],
        };
        using var stream = new MemoryStream();
        SetManifest.Write(stream, header, []);
        stream.Position = 0;

        var read = SetManifest.ReadHeader(stream);

        Assert.Equal(3, read.Databases.Count);
        Assert.Equal("shop", read.Databases[0].Name);
        Assert.Equal(10, read.Databases[0].Bytes);
        Assert.True(read.Databases[0].Ok);
        Assert.True(read.Databases[1].IsUsers);
        Assert.Null(read.Databases[1].Ok);
        Assert.Null(read.Databases[1].Sha256);
        Assert.Null(read.Databases[2].File);
        Assert.Equal("not there", read.Databases[2].Error);
    }

    [Theory]
    [InlineData("shop", "shop")]
    [InlineData("old site", "old_site")]
    [InlineData("сайт", "____")]
    [InlineData("_users", "__users")]
    [InlineData(".hidden", "_.hidden")]
    public void Dump_file_names_are_safe(string database, string expected)
    {
        Assert.Equal(expected, DbTools.FileName(database, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DbTools.UsersFileStem }));
    }

    [Fact]
    public void Dump_file_names_are_unique()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("a_b", DbTools.FileName("a b", used));
        Assert.Equal("a_b_2", DbTools.FileName("a-b".Replace('-', '_'), used));
        Assert.Equal("A_B_3", DbTools.FileName("A B", used));
    }

    [Theory]
    [InlineData("postgresql", "--\n-- PostgreSQL database dump complete\n--\n\n", true)]
    [InlineData("postgresql", "--\n-- PostgreSQL database dump complete\n--\n\n\\unrestrict Xyz\n\n", true)]
    [InlineData("postgresql", "--\n-- PostgreSQL database cluster dump complete\n--\n\n", true)]
    [InlineData("postgresql", "--\n-- PostgreSQL database dump complete\n--\nCREATE TABLE x();\n", false)]
    [InlineData("postgresql", "CREATE TABLE x();\n", false)]
    [InlineData("mysql", "INSERT ...;\n-- Dump completed on 2026-11-01 10:00:00\n", true)]
    [InlineData("mariadb", "INSERT ...;\r\n-- Dump completed on 2026-11-01 10:00:00\r\n\r\n", true)]
    [InlineData("mysql", "INSERT ...;\n", false)]
    [InlineData("mysql", "-- Dump completed\n-- CUSTOS: THIS DUMP IS INCOMPLETE: lost\n", false)]
    public void End_lines(string engine, string tail, bool complete)
    {
        Assert.Equal(complete, DumpCheck.HasEndLine(engine, Encoding.UTF8.GetBytes(tail)));
    }

    [Fact]
    public void Folders_of_panels_and_installers_are_found()
    {
        string drive = _temp.Combine("C");
        MakeTools(drive, "OSPanel/modules/MySQL-8.0/bin", "mysqldump", "mysql", "mysqld");
        MakeTools(drive, "OSPanel/modules/MariaDB-11.4/bin", "mariadb-dump", "mariadb", "mysqldump", "mysql");
        MakeTools(drive, "OSPanel/modules/PostgreSQL-17/bin", "pg_dump", "pg_dumpall", "psql");
        MakeTools(drive, "OSPanel/data/MySQL-8.0/bin", "mysqldump", "mysql");
        MakeTools(drive, "Program Files/PostgreSQL/16/bin", "pg_dump", "pg_dumpall", "psql");
        MakeTools(drive, "Tools/laragon/bin/mysql/mysql-8.0.30-winx64/bin", "mysqldump", "mysql");
        MakeTools(drive, "Games/mysql/bin", "notes");

        var found = DbServerFinder.Scan([drive], TestContext.Current.CancellationToken);

        Assert.Equal(5, found.Count);
        Assert.Contains(found, f => f.Engine == DbEngines.MySql && f.Version == "8.0" && f.BinDir.EndsWith(Path.Combine("MySQL-8.0", "bin"), StringComparison.Ordinal));
        Assert.Contains(found, f => f.Engine == DbEngines.MariaDb && f.Version == "11.4");
        Assert.Contains(found, f => f.Engine == DbEngines.PostgreSql && f.Version == "17");
        Assert.Contains(found, f => f.Engine == DbEngines.PostgreSql && f.Version == "16");
        Assert.Contains(found, f => f.Engine == DbEngines.MySql && f.Version == "8.0.30");
        Assert.DoesNotContain(found, f => f.BinDir.Contains("data", StringComparison.Ordinal));
    }

    private static void MakeTools(string root, string folder, params string[] tools)
    {
        string path = Path.Combine([root, .. folder.Split('/')]);
        Directory.CreateDirectory(path);
        foreach (string tool in tools)
        {
            File.WriteAllText(Path.Combine(path, DbTools.ExeName(tool)), string.Empty);
        }
    }

    private TaskDefinition NewTask(string format) => new()
    {
        Name = "Docs",
        BackupType = BackupTypes.Full,
        Sources = [new TaskSource { Path = _source }],
        Destinations = [new TaskDestination { Kind = DestinationKinds.Local, Path = _backup }],
        DbServers = [new TaskDatabaseServer { Engine = DbEngines.MySql, BinDir = _temp.Path, CredentialId = "root-account" }],
        Vss = new VssOptions { Enabled = false },
        Copy = new CopyOptions { ReadRetries = 0 },
        Archive = new ArchiveOptions { Format = format },
    };

    private ArchiveAccess Access() => new(_platform.Archives, null);

    private BackupResult Run(TaskDefinition task, DateTimeOffset startedAt)
    {
        var engine = new BackupEngine(_platform, _store, _log);
        using var gate = new PauseGate();
        return engine.Run(new BackupRequest
        {
            Task = task,
            RunId = Guid.NewGuid().ToString(),
            Trigger = RunTriggers.Manual,
            StartedAt = startedAt,
            TimeZone = TimeZoneInfo.Utc,
        }, gate, TestContext.Current.CancellationToken);
    }

    private RestoreResult Restore(IReadOnlyList<StoredSet> sets, StoredSet set, string? target, string? dumpsFolder) =>
        new RestoreEngine(_log).Run(
            new RestoreRequest { Sets = sets, Set = set, TargetFolder = target, DumpsFolder = dumpsFolder, Existing = RestoreExisting.Overwrite, Access = Access() },
            TestContext.Current.CancellationToken);

    private Dictionary<string, string> Unpack(StoredSet set)
    {
        using var reader = Access().Open(set);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var buffers = new Dictionary<int, MemoryStream>();
        reader.Extract(
            [.. reader.Entries.Where(e => !e.IsFolder).Select(e => e.Index)],
            e => buffers[e.Index] = new MemoryStream(),
            (e, outcome) => result[ArchivePaths.ToWindows(e.Path)] = Encoding.UTF8.GetString(buffers[e.Index].ToArray()),
            TestContext.Current.CancellationToken);
        return result;
    }

    private static SetManifestHeader ReadHeader(string set)
    {
        using var stream = File.OpenRead(Path.Combine(set, SetManifest.FileName));
        return SetManifest.ReadHeader(stream);
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(parts));

    private static string Sha256(string text) => SetManifest.ToHex(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
