using System.Text;
using System.Text.Json.Nodes;
using Custos.Core.Agent;
using Custos.Core.Cloud;
using Custos.Core.Model;
using Custos.Core.Transfer;

namespace Custos.Core.Tests;

/// <summary>
/// Export of tasks to a file and its import on a reinstalled or another computer (section 9a): two configuration
/// stores stand for the old and the new Windows. Nothing here leaves the test folder.
/// </summary>
public sealed class TaskExportTests : IDisposable
{
    private const string ExportPassword = "correct horse battery";
    private const string SharePassword = "share-pw";
    private const string ArchivePassword = "archive-pw";
    private const string DriveToken = "refresh-token-1";

    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly TempFolder _temp = new();
    private readonly ConfigStore _old;
    private readonly ConfigStore _new;
    private readonly Credential _share;
    private readonly Credential _archive;
    private readonly Credential _db;
    private readonly Credential _key;
    private readonly Credential _drive;
    private readonly Credential _unused;
    private readonly TaskDefinition _docs;
    private readonly TaskDefinition _site;

    public TaskExportTests()
    {
        _old = new ConfigStore(_temp.Combine("old"), new ReversingProtector());
        _old.Load();
        _new = new ConfigStore(_temp.Combine("new"), new ReversingProtector());
        _new.Load();

        _share = _old.SaveCredential(new Credential { User = "backup-user", Domain = "EXAMPLE" }, SharePassword);
        _archive = _old.SaveCredential(new Credential { Kind = CredentialKinds.ArchivePassword, User = "Archive password: Docs" }, ArchivePassword);
        _db = _old.SaveCredential(new Credential { Kind = CredentialKinds.Db, User = "root" }, null);
        _key = _old.CreateSshKey("custos@vps.example.org", "custos@OLD-PC");
        _drive = _old.SaveCloudAccount(null, CredentialKinds.GDrive, "backup@example.org", DriveToken, "own-app");
        _unused = _old.SaveCredential(new Credential { User = "nobody" }, "unused-pw");
        _old.SaveCloudApps(new CloudAppsFile
        {
            Gdrive = new CloudApp { ClientId = "own-app", ClientSecret = "own-secret" },
            Dropbox = new CloudApp { ClientId = "another-app" },
        });

        _docs = _old.SaveTask(new TaskDefinition
        {
            Name = "Docs",
            Sources = [new TaskSource { Path = @"C:\Data" }],
            Destinations = [new TaskDestination { Kind = DestinationKinds.Unc, Path = @"\\nas\backup", CredentialId = _share.Id }],
            DbServers = [new TaskDatabaseServer { Engine = DbEngines.MySql, BinDir = @"C:\db\bin", CredentialId = _db.Id }],
            Archive = new ArchiveOptions { Format = ArchiveFormats.Zip, Encrypt = true, PasswordCredentialId = _archive.Id },
        }, null, "local:a", Now).Saved;
        _site = _old.SaveTask(new TaskDefinition
        {
            Name = "Site",
            Sources = [new TaskSource { Kind = SourceKinds.Ssh, Host = "vps.example.org", User = "custos", CredentialId = _key.Id }],
            Destinations = [new TaskDestination { Kind = DestinationKinds.GDrive, Path = "site", CredentialId = _drive.Id }],
        }, null, "local:a", Now).Saved;
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void A_file_without_a_password_holds_the_tasks_and_their_accounts_but_no_secrets()
    {
        var file = Export(password: null);
        string json = Encoding.UTF8.GetString(TaskExport.Serialize(file));

        Assert.Null(file.Secrets);
        Assert.Equal([_docs.Id, _site.Id], file.Tasks.Select(t => t.Id));
        Assert.Equal(
            new[] { _share.Id, _archive.Id, _db.Id, _key.Id, _drive.Id }.Order(),
            file.Credentials.Select(c => c.Id).Order());
        Assert.DoesNotContain(_unused.Id, json, StringComparison.Ordinal);
        foreach (string secret in new[] { SharePassword, ArchivePassword, DriveToken, "own-secret", "secret_dpapi" })
        {
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        }

        var read = TaskExport.Read(Encoding.UTF8.GetBytes(json));
        Assert.Equal("OLD-PC", read.Computer);
        Assert.Equal(Now, read.ExportedAt);
        Assert.Equal(_key.PublicKey, read.Credentials.Single(c => c.Id == _key.Id).PublicKey);
    }

    [Fact]
    public void The_secrets_open_only_with_the_export_password()
    {
        var file = TaskExport.Read(TaskExport.Serialize(Export(ExportPassword)));
        string json = Encoding.UTF8.GetString(TaskExport.Serialize(file));
        Assert.DoesNotContain(SharePassword, json, StringComparison.Ordinal);
        Assert.DoesNotContain(DriveToken, json, StringComparison.Ordinal);

        var secrets = TaskExport.Open(file, ExportPassword);

        Assert.Equal(SharePassword, secrets.Credentials[_share.Id]);
        Assert.Equal(ArchivePassword, secrets.Credentials[_archive.Id]);
        Assert.Equal(DriveToken, secrets.Credentials[_drive.Id]);
        Assert.Equal(_old.GetPassword(_key.Id), secrets.Credentials[_key.Id]);
        Assert.False(secrets.Credentials.ContainsKey(_db.Id));
        Assert.False(secrets.Credentials.ContainsKey(_unused.Id));

        // Only the own application that issued an exported token goes along.
        Assert.Equal("own-secret", secrets.CloudApps?.Gdrive?.ClientSecret);
        Assert.Null(secrets.CloudApps?.Dropbox);

        var wrong = Assert.Throws<ExportException>(() => TaskExport.Open(file, "wrong password"));
        Assert.True(wrong.IsWrongPassword);
    }

    [Fact]
    public void A_short_export_password_is_refused() =>
        Assert.Throws<ExportException>(() => Export(new string('x', TaskExport.MinPasswordLength - 1)));

    [Fact]
    public void A_changed_secret_part_does_not_open()
    {
        var file = Export(ExportPassword);
        byte[] data = Convert.FromBase64String(file.Secrets!.Data);
        data[3] ^= 1;
        file.Secrets.Data = Convert.ToBase64String(data);

        var error = Assert.Throws<ExportException>(() => TaskExport.Open(file, ExportPassword));
        Assert.True(error.IsWrongPassword);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json at all")]
    [InlineData("{11//\nName=Documents\n//11}")]
    [InlineData("""{"format":"custos-tasks","version":2,"tasks":[]}""")]
    public void Other_files_are_refused(string text) =>
        Assert.Throws<ExportException>(() => TaskExport.Read(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void A_file_whose_task_refers_to_a_missing_account_is_refused()
    {
        var node = JsonNode.Parse(TaskExport.Serialize(Export(password: null)))!;
        var accounts = node["credentials"]!.AsArray();
        accounts.Remove(accounts.First(a => (string?)a!["id"] == _share.Id));

        Assert.Throws<ExportException>(() => TaskExport.Read(Encoding.UTF8.GetBytes(node.ToJsonString())));
    }

    [Fact]
    public void Used_accounts_include_the_archive_password_and_database_servers() =>
        Assert.Equal(new[] { _share.Id, _archive.Id, _db.Id }.Order(), TaskExport.UsedCredentialIds([_docs]).Order());

    [Fact]
    public void With_passwords_a_reinstalled_computer_gets_everything_back()
    {
        var file = TaskExport.Read(TaskExport.Serialize(Export(ExportPassword)));
        var plan = new TaskImport(file, TaskExport.Open(file, ExportPassword), _new.GetTasks(), _new.GetCredentials());

        Assert.All(plan.Items, i => Assert.Empty(i.Remarks));
        Assert.Empty(plan.AccountsToAsk(plan.Items));

        var build = plan.Build(plan.Items, Empty, Empty);
        Assert.Empty(build.SwitchedOff);
        Assert.True(build.Request.KeepIds);
        Assert.Equal("own-app", build.Request.CloudApps?.Gdrive?.ClientId);
        var changes = Import(_new, build);

        // The same ids: «Restore» finds the sets the tasks made before, retention keeps managing them.
        Assert.All(changes, c => Assert.Null(c.Previous));
        Assert.Equal([_docs.Id, _site.Id], _new.GetTasks().Select(t => t.Id));
        Assert.All(_new.GetTasks(), t => Assert.True(t.Enabled));
        Assert.Equal(SharePassword, _new.GetPassword(_share.Id));
        Assert.Equal(ArchivePassword, _new.GetPassword(_archive.Id));
        Assert.Equal(DriveToken, _new.GetPassword(_drive.Id));
        Assert.Equal("own-app", _new.GetCredential(_drive.Id)!.ClientId);
        Assert.Equal(_old.GetSshKey(_key.Id), _new.GetSshKey(_key.Id));
        Assert.Equal(_key.PublicKey, _new.GetCredential(_key.Id)!.PublicKey);
        Assert.Null(_new.GetPassword(_db.Id));
    }

    [Fact]
    public void Without_passwords_the_import_asks_for_them_and_switches_off_what_still_lacks()
    {
        var file = TaskExport.Read(TaskExport.Serialize(Export(password: null)));
        var plan = new TaskImport(file, null, _new.GetTasks(), _new.GetCredentials());

        Assert.Equal(
            [ImportRemarkCodes.ArchivePasswordMissing, ImportRemarkCodes.PasswordMissing],
            plan.Items[0].Remarks.Select(r => r.Code).Order());
        Assert.Equal(
            [ImportRemarkCodes.CloudMissing, ImportRemarkCodes.SshKeyMissing],
            plan.Items[1].Remarks.Select(r => r.Code).Order());

        // Typed are only passwords; the database account had none, keys and cloud accounts are made again here.
        var asked = plan.AccountsToAsk(plan.Items);
        Assert.Equal(new[] { _share.Id, _archive.Id }.Order(), asked.Select(a => a.Credential.Id).Order());
        Assert.Equal(["Docs"], asked.Single(a => a.Credential.Id == _share.Id).TaskNames);

        var typed = new Dictionary<string, string> { [_share.Id] = SharePassword, [_archive.Id] = string.Empty };
        var build = plan.Build(plan.Items, typed, Empty);
        Assert.Equal(["Docs", "Site"], build.SwitchedOff);
        Import(_new, build);

        Assert.All(_new.GetTasks(), t => Assert.False(t.Enabled));
        Assert.Equal(SharePassword, _new.GetPassword(_share.Id));
        Assert.False(_new.GetCredential(_archive.Id)!.HasSecret);
        Assert.False(_new.GetCredential(_key.Id)!.HasSecret);
        Assert.Equal(_key.PublicKey, _new.GetCredential(_key.Id)!.PublicKey);
        Assert.False(_new.GetCredential(_drive.Id)!.HasSecret);

        // Typed later in the editor, the archive password fills the account the task already points to.
        _new.SaveCredential(_new.GetCredential(_archive.Id)!, ArchivePassword);
        Assert.Equal(ArchivePassword, _new.GetPassword(_archive.Id));
    }

    [Fact]
    public void A_file_not_opened_yet_says_so_instead_of_saying_the_passwords_are_missing()
    {
        var file = TaskExport.Read(TaskExport.Serialize(Export(ExportPassword)));
        var plan = new TaskImport(file, null, _new.GetTasks(), _new.GetCredentials());

        Assert.True(plan.SecretsLocked);
        Assert.All(plan.Items, i => Assert.Equal(ImportRemarkCodes.SecretsLocked, Assert.Single(i.Remarks).Code));

        // Declined, the passwords can still be typed as for a file without them.
        Assert.Equal(new[] { _share.Id, _archive.Id }.Order(), plan.AccountsToAsk(plan.Items).Select(a => a.Credential.Id).Order());

        var opened = new TaskImport(file, TaskExport.Open(file, ExportPassword), _new.GetTasks(), _new.GetCredentials());
        Assert.False(opened.SecretsLocked);
        Assert.All(opened.Items, i => Assert.Empty(i.Remarks));
    }

    [Fact]
    public void A_task_that_only_misses_a_typed_password_stays_on_once_it_is_typed()
    {
        var file = TaskExport.Read(TaskExport.Serialize(Export(password: null, _docs)));
        var plan = new TaskImport(file, null, [], []);

        var build = plan.Build(plan.Items, new Dictionary<string, string> { [_share.Id] = SharePassword, [_archive.Id] = ArchivePassword }, Empty);

        Assert.Empty(build.SwitchedOff);
        Assert.True(Assert.Single(build.Request.Tasks).Enabled);
    }

    [Fact]
    public void A_task_already_here_is_replaced_and_the_accounts_here_are_kept()
    {
        var file = TaskExport.Read(TaskExport.Serialize(Export(password: null)));
        file.Tasks[0].Comment = "from the file";
        var plan = new TaskImport(file, null, _old.GetTasks(), _old.GetCredentials());

        Assert.All(plan.Items, i => Assert.NotNull(i.Here));
        Assert.All(plan.Items, i => Assert.Equal(ImportRemarkCodes.Replaces, Assert.Single(i.Remarks).Code));
        Assert.Empty(plan.AccountsToAsk(plan.Items));

        var build = plan.Build(plan.Items, Empty, Empty);
        Assert.Empty(build.Request.Credentials);
        Assert.Empty(build.SwitchedOff);
        var changes = Import(_old, build);

        Assert.All(changes, c => Assert.NotNull(c.Previous));
        var tasks = _old.GetTasks();
        Assert.Equal([_docs.Id, _site.Id], tasks.Select(t => t.Id));
        Assert.Equal("from the file", tasks[0].Comment);
        Assert.Equal(2, tasks[0].Revision);
        Assert.Equal(SharePassword, _old.GetPassword(_share.Id));
    }

    [Fact]
    public void An_account_here_without_a_password_takes_the_one_from_the_file()
    {
        _new.Import([], [(_share, null)], "local:b", Now, keepIds: true);
        var file = TaskExport.Read(TaskExport.Serialize(Export(ExportPassword, _docs)));
        var plan = new TaskImport(file, TaskExport.Open(file, ExportPassword), _new.GetTasks(), _new.GetCredentials());

        Import(_new, plan.Build(plan.Items, Empty, Empty));

        Assert.Equal(SharePassword, _new.GetPassword(_share.Id));
        Assert.Single(_new.GetCredentials(), c => c.Id == _share.Id);
    }

    [Fact]
    public void A_similar_account_here_can_be_used_instead_of_typing_a_password()
    {
        var here = _new.SaveCredential(new Credential { User = "BACKUP-USER", Domain = "example" }, "stored-pw");
        var file = TaskExport.Read(TaskExport.Serialize(Export(password: null, _docs)));
        var plan = new TaskImport(file, null, _new.GetTasks(), _new.GetCredentials());

        var share = plan.AccountsToAsk(plan.Items).Single(a => a.Credential.Id == _share.Id);
        Assert.Equal(here.Id, share.Similar?.Id);
        Assert.Null(plan.AccountsToAsk(plan.Items).Single(a => a.Credential.Id == _archive.Id).Similar);

        var build = plan.Build(plan.Items, new Dictionary<string, string> { [_archive.Id] = ArchivePassword }, new Dictionary<string, string> { [_share.Id] = here.Id });
        Import(_new, build);

        Assert.Equal(here.Id, _new.GetTasks().Single().Destinations[0].CredentialId);
        Assert.DoesNotContain(_new.GetCredentials(), c => c.Id == _share.Id);
        Assert.True(_new.GetTasks().Single().Enabled);
    }

    [Fact]
    public void Without_keeping_ids_an_import_never_replaces_anything()
    {
        var changes = _old.Import([_docs], [], "local:a", Now);

        Assert.NotEqual(_docs.Id, Assert.Single(changes).Saved.Id);
        Assert.Equal(3, _old.GetTasks().Count);
        Assert.Throws<ConfigException>(() => _old.Import([], [(_share, "x")], "local:a", Now));
    }

    private static readonly Dictionary<string, string> Empty = [];

    private ExportFile Export(string? password, params TaskDefinition[] tasks) => TaskExport.Create(
        tasks.Length == 0 ? _old.GetTasks() : tasks,
        _old.GetCredentials(),
        password is null ? null : _old.GetPassword,
        password is null ? null : _old.GetCloudApps(),
        password,
        "OLD-PC",
        Now);

    private static List<TaskChange> Import(ConfigStore store, ImportBuild build) =>
        store.Import(build.Request.Tasks, [.. build.Request.Credentials.Select(c => (c.Credential, c.Password))], "local:b", Now, keepIds: build.Request.KeepIds);

    private sealed class ReversingProtector : ISecretProtector
    {
        public byte[] Protect(byte[] data) => [.. data.Reverse()];

        public byte[] Unprotect(byte[] data) => [.. data.Reverse()];
    }
}
