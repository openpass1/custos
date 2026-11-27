using System.IO.Compression;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using Custos.Core;
using Custos.Core.Backup;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Service;
using Custos.Service.Platform;

namespace Custos.Linux.Tests;

/// <summary>
/// The service of Linux, started inside the test process (<see cref="ServiceFixture"/>), driven over its socket as the
/// command custos and the site drive it: tasks are saved and run, copies land in the destination and come back with
/// «Восстановить». Names that differ only in case are two files on Linux, and stay two.
/// </summary>
[Collection(ServiceGroup.Name)]
public sealed class ServiceTests(ServiceFixture service)
{
    internal static readonly string[] Files = ["a.txt", "sub/b.txt", "отчёт за ноябрь.txt", "Report.txt", "report.txt"];

    [Fact]
    public void Hello_answers_with_the_version_and_the_user()
    {
        Assert.Equal(PipeProtocol.Version, service.Hello.Protocol);
        Assert.Equal(CustosInfo.Version, service.Hello.ServiceVersion);
        Assert.Equal(Environment.UserName, service.Hello.User);
    }

    [Fact]
    public async Task A_folder_copy_keeps_names_that_differ_in_case_and_comes_back()
    {
        var (source, destination) = Folders("copy");
        var task = await service.SaveAsync(NewTask("Linux copy", source, destination));

        var run = await service.RunAsync(task.Id);

        Assert.True(run.Status == RunStatuses.Success, run.ErrorMessage);
        Assert.Equal(Files.Length, run.FilesProcessed);
        foreach (string name in Files)
        {
            string copied = Assert.Single(Directory.GetFiles(destination, Path.GetFileName(name), SearchOption.AllDirectories));
            Assert.Equal(Content(name), await File.ReadAllTextAsync(copied, TestContext.Current.CancellationToken));
        }

        // A second run copies nothing new: the change tracking knows both names.
        task.BackupType = BackupTypes.Incremental;
        task = await service.SaveAsync(task, task.Revision);
        var second = await service.RunAsync(task.Id);
        Assert.True(second.Status == RunStatuses.Success, second.ErrorMessage);
        Assert.Equal(0, second.FilesProcessed);

        await RestoreAndCompareAsync(task.Id, "folder");
    }

    [Fact]
    public async Task A_zip_archive_is_written_and_restored()
    {
        var (source, destination) = Folders("zip");
        var definition = NewTask("Linux zip", source, destination);
        definition.Archive.Format = ArchiveFormats.Zip;
        var task = await service.SaveAsync(definition);

        var run = await service.RunAsync(task.Id);

        Assert.True(run.Status == RunStatuses.Success, run.ErrorMessage);
        Assert.NotEmpty(Directory.GetFiles(destination, "*.zip", SearchOption.AllDirectories));
        await RestoreAndCompareAsync(task.Id, ArchiveFormats.Zip);
    }

    [Fact]
    public async Task A_backup_skips_special_files_and_directory_links_but_keeps_regular_file_links()
    {
        string source = TestPlace.NewFolder("special-src");
        string destination = TestPlace.NewFolder("special-dst");
        string outside = TestPlace.NewFolder("special-outside");
        string folder = Directory.CreateDirectory(Path.Combine(source, "files")).FullName;
        string regular = Path.Combine(folder, "ordinary.txt");
        File.WriteAllText(regular, "file contents");
        File.WriteAllText(Path.Combine(outside, "external.txt"), "not selected");

        File.CreateSymbolicLink(Path.Combine(source, "regular-link.txt"), regular);
        Directory.CreateSymbolicLink(Path.Combine(source, "directory-link"), outside);
        string fifo = Path.Combine(source, "live-fifo");
        Assert.Equal(0, Tool.Run("mkfifo", [fifo]).ExitCode);
        File.CreateSymbolicLink(Path.Combine(source, "fifo-link"), fifo);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(Path.Combine(source, "live.socket")));

        var task = await service.SaveAsync(NewTask("Linux file types", source, destination));
        var run = await service.RunAsync(task.Id);

        Assert.True(run.Status == RunStatuses.Success, run.ErrorMessage);
        Assert.Equal(2, run.FilesProcessed);
        string copied = Assert.Single(Directory.GetFiles(destination, "ordinary.txt", SearchOption.AllDirectories));
        string linked = Assert.Single(Directory.GetFiles(destination, "regular-link.txt", SearchOption.AllDirectories));
        Assert.Equal("file contents", File.ReadAllText(copied));
        Assert.Equal("file contents", File.ReadAllText(linked));
        Assert.Empty(Directory.GetFiles(destination, "external.txt", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(destination, "live*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(destination, "fifo-link", SearchOption.AllDirectories));

        var platform = new LinuxBackupPlatform(new Custos.Core.Agent.ConfigStore(DataFolders.Config, new KeyFileProtector()), new ShadowCopies(DataFolders.PendingShadowsFile));
        Assert.Throws<IOException>(() => platform.OpenSourceFile(fifo, 1));
    }

    [Fact]
    public async Task Files_with_metadata_errors_are_reported_and_the_rest_of_the_source_is_copied()
    {
        var (source, destination) = Folders("source-metadata");
        string dangling = Path.Combine(source, "missing-link");
        string loop = Path.Combine(source, "looping-link");
        File.CreateSymbolicLink(dangling, "missing-target");
        File.CreateSymbolicLink(loop, loop);
        var task = await service.SaveAsync(NewTask("Linux source metadata", source, destination));

        var run = await service.RunAsync(task.Id);

        Assert.Equal(RunStatuses.Warning, run.Status);
        Assert.Equal(Files.Length, run.FilesProcessed);
        var log = await service.RunLogAsync(run.RunId);
        Assert.Contains(log, line => line.Contains(dangling, StringComparison.Ordinal));
        Assert.Contains(log, line => line.Contains(loop, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ArchiveFormats.None)]
    [InlineData(ArchiveFormats.Zip)]
    public void A_file_deleted_after_the_scan_is_logged_and_the_case_distinct_destination_still_works(string format)
    {
        string parent = TestPlace.NewFolder("vanished");
        string source = Directory.CreateDirectory(Path.Combine(parent, "Source")).FullName;
        string destination = Path.Combine(parent, "source");
        string vanished = Path.Combine(source, "gone.txt");
        File.WriteAllText(vanished, "short-lived data");
        File.WriteAllText(Path.Combine(source, "kept.txt"), "lasting data");
        var task = NewTask("Linux disappearing file", source, destination);
        task.Archive.Format = format;
        var platform = new LinuxBackupPlatform(new Custos.Core.Agent.ConfigStore(DataFolders.Config, new KeyFileProtector()), new ShadowCopies(DataFolders.PendingShadowsFile));
        var log = new CapturedRunLog();
        var engine = new BackupEngine(platform, new MemoryFileStateStore(), log, progress =>
        {
            if (progress.Phase == BackupPhases.Copy)
            {
                File.Delete(vanished);
            }
        });
        using var pause = new PauseGate();
        var result = engine.Run(new BackupRequest
        {
            Task = task,
            RunId = TaskDefinition.NewId(),
            Trigger = RunTriggers.Manual,
            StartedAt = DateTimeOffset.UtcNow,
        }, pause, TestContext.Current.CancellationToken);

        Assert.Equal(RunStatuses.Success, result.Status);
        Assert.Equal(1, result.FilesProcessed);
        Assert.Contains(log.Lines, line => line.Level == RunLogLevel.Info && line.Message.Contains(vanished, StringComparison.Ordinal));
    }

    private sealed class CapturedRunLog : IRunLog
    {
        public List<(RunLogLevel Level, string Message)> Lines { get; } = [];

        public void Write(RunLogLevel level, string message) => Lines.Add((level, message));
    }

    [Theory]
    [InlineData(ArchiveFormats.None)]
    [InlineData(ArchiveFormats.Zip)]
    public async Task Repeated_backups_exclude_a_nested_destination_and_its_previous_sets(string format)
    {
        string source = TestPlace.NewFolder("overlap-src");
        string destination = Directory.CreateDirectory(Path.Combine(source, "destination")).FullName;
        string outside = TestPlace.NewFolder("overlap-alias");
        string alias = Path.Combine(outside, "backup-link");
        Directory.CreateSymbolicLink(alias, destination);
        File.WriteAllText(Path.Combine(source, "selected.txt"), "backup data");
        string existing = Path.Combine(destination, "personal.txt");
        File.WriteAllText(existing, "already in the destination");
        var definition = NewTask("Linux nested destination", source, alias);
        definition.Archive.Format = format;
        var task = await service.SaveAsync(definition);

        for (int i = 0; i < 2; i++)
        {
            var run = await service.RunAsync(task.Id);
            Assert.Equal(RunStatuses.Warning, run.Status);
            Assert.Equal(1, run.FilesProcessed);
            Assert.Contains(await service.RunLogAsync(run.RunId), line => line.Contains(destination, StringComparison.Ordinal));
        }

        Assert.Equal("already in the destination", File.ReadAllText(existing));
        if (format == ArchiveFormats.None)
        {
            Assert.Equal(2, Directory.GetFiles(destination, "selected.txt", SearchOption.AllDirectories).Length);
            Assert.Equal(existing, Assert.Single(Directory.GetFiles(destination, "personal.txt", SearchOption.AllDirectories)));
        }
        else
        {
            string[] archives = Directory.GetFiles(destination, "*.zip", SearchOption.AllDirectories);
            Assert.Equal(2, archives.Length);
            foreach (string path in archives)
            {
                using var archive = ZipFile.OpenRead(path);
                Assert.Single(archive.Entries, entry => entry.Name == "selected.txt");
                Assert.DoesNotContain(archive.Entries, entry => entry.Name == "personal.txt" || entry.Name.EndsWith(".zip", StringComparison.Ordinal));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_destination_equal_to_or_containing_the_source_is_rejected(bool sameFolder)
    {
        string destination = TestPlace.NewFolder("unsafe-overlap");
        string source = sameFolder ? destination : Directory.CreateDirectory(Path.Combine(destination, "source")).FullName;
        string existing = Path.Combine(source, "selected.txt");
        File.WriteAllText(existing, "leave the source alone");
        var task = await service.SaveAsync(NewTask("Linux unsafe overlap", source, destination));

        var run = await service.RunAsync(task.Id);

        Assert.Equal(RunStatuses.Error, run.Status);
        Assert.Equal(existing, Assert.Single(Directory.GetFiles(destination, "*", SearchOption.AllDirectories)));
        Assert.Equal("leave the source alone", File.ReadAllText(existing));
    }

    /// <summary>7-Zip on Linux is 7z.so built from the sources by linux/build-native-linux.sh; CI puts it next to the tests.</summary>
    [Fact]
    public async Task An_encrypted_7z_archive_is_written_and_restored()
    {
        bool have7z = File.Exists(Path.Combine(AppContext.BaseDirectory, "7z.so"));
        if (!have7z && Environment.GetEnvironmentVariable("CUSTOS_REQUIRE_7Z") == "1")
        {
            Assert.Fail("7z.so is not next to the tests, and CUSTOS_REQUIRE_7Z=1 says it must be.");
        }

        Assert.SkipUnless(have7z, "7z.so is not next to the tests (build it with linux/build-native-linux.sh).");
        Assert.SkipUnless(service.Hello.IsAdmin, "Saving a password needs root or a member of sudo.");
        var password = await service.RequestAsync(PipeProtocol.Methods.CredentialSave,
            new CredentialInput { Credential = new Credential { Kind = CredentialKinds.ArchivePassword, User = "archive" }, Password = "Секрет-7z!" },
            IpcJsonContext.Default.CredentialInput, IpcJsonContext.Default.CredentialSaveResult);
        var (source, destination) = Folders("7z");
        var definition = NewTask("Linux 7z", source, destination);
        definition.Archive.Format = ArchiveFormats.SevenZip;
        definition.Archive.Encrypt = true;
        definition.Archive.EncryptNames = true;
        definition.Archive.PasswordCredentialId = password.Credential.Id;
        var task = await service.SaveAsync(definition);

        var run = await service.RunAsync(task.Id);

        Assert.True(run.Status == RunStatuses.Success, run.ErrorMessage);
        string archive = Assert.Single(Directory.GetFiles(destination, "*.7z", SearchOption.AllDirectories));
        Assert.DoesNotContain("отчёт", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(archive, TestContext.Current.CancellationToken)), StringComparison.Ordinal);
        await RestoreAndCompareAsync(task.Id, ArchiveFormats.SevenZip);
    }

    /// <summary>A source on a plain file system cannot have a snapshot: set to stop, the task stops with the reason; set
    /// to go on, it copies the files as they are with a warning. The state of the service says so too.</summary>
    [Fact]
    public async Task Without_a_volume_that_can_have_snapshots_a_task_stops_or_goes_on_as_set()
    {
        var (source, destination) = Folders("nosnap");
        Assert.SkipWhen(Disks.CanSnapshot(source), "The temporary folder of this computer can have snapshots.");
        var definition = NewTask("Linux no snapshot", source, destination);
        definition.Vss = new VssOptions { Enabled = true, OnFailure = VssFailureActions.Abort };
        var task = await service.SaveAsync(definition);

        var stopped = await service.RunAsync(task.Id);

        Assert.Equal(RunStatuses.Error, stopped.Status);
        Assert.Empty(Directory.GetFiles(destination, "a.txt", SearchOption.AllDirectories));

        task.Vss.OnFailure = VssFailureActions.Continue;
        task = await service.SaveAsync(task, task.Revision);
        var copied = await service.RunAsync(task.Id);

        Assert.Equal(RunStatuses.Warning, copied.Status);
        Assert.Single(Directory.GetFiles(destination, "a.txt", SearchOption.AllDirectories));
        var state = await ServiceFixture.WaitAsync(service.SnapshotAsync, s => s.Snapshots.Any(v => v.TaskIds.Contains(task.Id)));
        Assert.Equal(SnapshotStates.Unsupported, state.Snapshots.First(v => v.TaskIds.Contains(task.Id)).State);
    }

    /// <summary>A file system of the system mounted inside a source (here a tmpfs) is not read, as tar
    /// --one-file-system does not; the run log names it.</summary>
    [Fact]
    public async Task A_file_system_of_the_system_inside_a_source_is_skipped()
    {
        Assert.SkipUnless(TestPlace.IsRoot, "Mounting needs root.");
        var (source, destination) = Folders("skip");
        string inner = Path.Combine(source, "memory");
        Directory.CreateDirectory(inner);
        Disks.Run("mount", "-t", "tmpfs", "-o", "size=8m", "tmpfs", inner);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(inner, "in-memory.txt"), "x", TestContext.Current.CancellationToken);
            var task = await service.SaveAsync(NewTask("Linux skip", source, destination));

            var run = await service.RunAsync(task.Id);

            Assert.True(run.Status == RunStatuses.Success, run.ErrorMessage);
            Assert.Single(Directory.GetFiles(destination, "a.txt", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(destination, "in-memory.txt", SearchOption.AllDirectories));
            Assert.Contains(await service.RunLogAsync(run.RunId), l => l.Contains("tmpfs", StringComparison.Ordinal));
        }
        finally
        {
            Disks.Run("umount", inner);
        }
    }

    internal static TaskDefinition NewTask(string name, string source, string destination) => NewTask(name, [source], destination);

    internal static TaskDefinition NewTask(string name, string[] sources, string destination) => new()
    {
        Name = name + " " + Guid.NewGuid().ToString("N")[..4],
        BackupType = BackupTypes.Full,
        Sources = [.. sources.Select(s => new TaskSource { Kind = SourceKinds.Folder, Path = s })],
        Destinations = [new TaskDestination { Kind = DestinationKinds.Local, Path = destination }],
        Schedule = new ScheduleOptions { Type = ScheduleTypes.Manual },
        Vss = new VssOptions { Enabled = false },
    };

    internal static (string Source, string Destination) Folders(string name)
    {
        string source = TestPlace.NewFolder(name + "-src");
        WriteFiles(source);
        return (source, TestPlace.NewFolder(name + "-dst"));
    }

    internal static void WriteFiles(string folder)
    {
        foreach (string file in Files)
        {
            string path = Path.Combine(folder, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Content(file));
        }
    }

    internal static string Content(string name) => $"CUSTOS {name}\nстрока по-русски\n";

    /// <summary>The newest copy of the task, into a folder of its own; every file comes back as it was.</summary>
    private async Task RestoreAndCompareAsync(string taskId, string storage)
    {
        var list = await service.RequestAsync(PipeProtocol.Methods.RestoreList, new RestoreListRequest { TaskId = taskId },
            IpcJsonContext.Default.RestoreListRequest, IpcJsonContext.Default.RestoreListResult);
        Assert.NotEmpty(list.Sets);
        var set = list.Sets[^1];
        Assert.Equal(storage, set.Storage);

        string target = TestPlace.NewFolder("restored");
        await service.RequestAsync(PipeProtocol.Methods.RestoreStart,
            new RestoreStartRequest { TaskId = taskId, Location = set.Location, Where = set.Where, TargetFolder = target },
            IpcJsonContext.Default.RestoreStartRequest, IpcJsonContext.Default.OkResult);
        var status = await ServiceFixture.WaitAsync(
            () => service.RequestAsync(PipeProtocol.Methods.RestoreStatus, EmptyRequest.Instance, IpcJsonContext.Default.EmptyRequest, IpcJsonContext.Default.RestoreStatusResult),
            s => !s.Running && s.Result is not null);

        Assert.True(status.Result!.Status == RunStatuses.Success, status.Result.ErrorMessage);
        foreach (string name in Files)
        {
            string restored = Assert.Single(Directory.GetFiles(target, Path.GetFileName(name), SearchOption.AllDirectories));
            Assert.Equal(Content(name), await File.ReadAllTextAsync(restored, TestContext.Current.CancellationToken));
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ServiceGroup : ICollectionFixture<ServiceFixture>
{
    public const string Name = "service";
}

/// <summary>
/// The service of Linux inside the test process, and one connection to it as the command custos has. With
/// CUSTOS_INSTALLED_SERVICE=1 the connection goes to the installed service instead, and none is started.
/// </summary>
public sealed class ServiceFixture : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private readonly CancellationTokenSource _stop = new();
    private Task<int>? _service;
    private ServiceClient? _client;

    public HelloResult Hello { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        if (!TestPlace.Installed)
        {
            _service = Task.Run(() => AgentHost.RunAsync([], _stop.Token));
        }

        _client = await ServiceClient.ConnectAsync(null, Timeout, CancellationToken.None);
        Hello = await RequestAsync(PipeProtocol.Methods.Hello, new HelloRequest { Protocol = PipeProtocol.Version, ClientVersion = CustosInfo.Version },
            IpcJsonContext.Default.HelloRequest, IpcJsonContext.Default.HelloResult);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        await _stop.CancelAsync();
        if (_service is not null)
        {
            int code = await _service.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(0, code);
        }

        _stop.Dispose();
    }

    public Task<TResult> RequestAsync<TParams, TResult>(string method, TParams parameters, JsonTypeInfo<TParams> parametersType, JsonTypeInfo<TResult> resultType) =>
        _client!.RequestAsync(method, parameters, parametersType, resultType, CancellationToken.None).WaitAsync(Timeout);

    /// <summary>A new task, or a change of one saved before (<paramref name="baseRevision"/>).</summary>
    public async Task<TaskDefinition> SaveAsync(TaskDefinition task, int? baseRevision = null)
    {
        Assert.SkipUnless(Hello.IsAdmin, "Changing tasks needs root or a member of sudo.");
        var saved = await RequestAsync(PipeProtocol.Methods.TaskSave, new TaskSaveRequest { Task = task, BaseRevision = baseRevision },
            IpcJsonContext.Default.TaskSaveRequest, IpcJsonContext.Default.TaskSaveResult);
        return saved.Task;
    }

    /// <summary>«Запустить», and the record of the run once it has ended.</summary>
    public async Task<RunRecord> RunAsync(string taskId)
    {
        var before = await SnapshotAsync();
        string? previous = before.States.FirstOrDefault(s => s.TaskId == taskId)?.LastRun?.RunId;
        var started = await RequestAsync(PipeProtocol.Methods.TaskRun, new TaskRequest { TaskId = taskId },
            IpcJsonContext.Default.TaskRequest, IpcJsonContext.Default.OkResult);
        Assert.True(started.Ok, started.Message);
        var snapshot = await WaitAsync(SnapshotAsync, s =>
            s.Current is null && s.Queue.Count == 0
            && s.States.FirstOrDefault(t => t.TaskId == taskId)?.LastRun is { Status: not null } last && last.RunId != previous);
        return snapshot.States.First(t => t.TaskId == taskId).LastRun!;
    }

    public async Task<List<string>> RunLogAsync(string runId)
    {
        var log = await RequestAsync(PipeProtocol.Methods.RunLog, new RunLogRequest { RunId = runId },
            IpcJsonContext.Default.RunLogRequest, IpcJsonContext.Default.RunLogResult);
        return [.. log.Lines.Select(l => l.Text)];
    }

    public static async Task<T> WaitAsync<T>(Func<Task<T>> ask, Func<T, bool> done)
    {
        var until = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (true)
        {
            var value = await ask();
            if (done(value))
            {
                return value;
            }

            if (DateTime.UtcNow > until)
            {
                Assert.Fail("The service did not finish in 3 minutes.");
            }

            await Task.Delay(250);
        }
    }

    public Task<AgentSnapshot> SnapshotAsync() =>
        RequestAsync(PipeProtocol.Methods.Snapshot, EmptyRequest.Instance, IpcJsonContext.Default.EmptyRequest, IpcJsonContext.Default.AgentSnapshot);
}
