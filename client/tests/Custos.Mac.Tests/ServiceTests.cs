using System.Text;
using System.Text.Json.Serialization.Metadata;
using Custos.Core;
using Custos.Core.Backup;
using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Service;
using Custos.Service.Platform;

namespace Custos.Mac.Tests;

/// <summary>
/// The service of macOS, started inside the test process (<see cref="ServiceFixture"/>), driven over its socket as the
/// window drives it: tasks are saved and run, copies land in the destination and come back with «Восстановить».
/// Restoring needs an administrator: the test runner of GitHub on macOS is one; elsewhere those parts are skipped.
/// </summary>
[Collection(ServiceGroup.Name)]
public sealed class ServiceTests(ServiceFixture service)
{
    internal static readonly string[] Files = ["a.txt", "sub/b.txt", "отчёт за ноябрь.txt"];

    [Fact]
    public void Hello_answers_with_the_version_and_the_user()
    {
        Assert.Equal(PipeProtocol.Version, service.Hello.Protocol);
        Assert.Equal(CustosInfo.Version, service.Hello.ServiceVersion);
        Assert.Equal(Environment.UserName, service.Hello.User);
    }

    [Fact]
    public async Task A_folder_copy_lands_in_the_destination_and_comes_back()
    {
        var (source, destination) = Folders("copy");
        var task = await service.SaveAsync(NewTask("Mac copy", source, destination));

        var run = await service.RunAsync(task.Id);

        Assert.Equal(RunStatuses.Success, run.Status);
        foreach (string name in Files)
        {
            string copied = Assert.Single(Directory.GetFiles(destination, Path.GetFileName(name), SearchOption.AllDirectories));
            Assert.Equal(Content(name), await File.ReadAllTextAsync(copied, TestContext.Current.CancellationToken));
        }

        var history = await service.RequestAsync(PipeProtocol.Methods.HistoryGet, new HistoryRequest { TaskId = task.Id },
            IpcJsonContext.Default.HistoryRequest, IpcJsonContext.Default.HistoryResult);
        Assert.Equal(run.RunId, Assert.Single(history.Runs).RunId);

        var log = await service.RequestAsync(PipeProtocol.Methods.RunLog, new RunLogRequest { RunId = run.RunId },
            IpcJsonContext.Default.RunLogRequest, IpcJsonContext.Default.RunLogResult);
        Assert.True(log.Found);
        Assert.NotEmpty(log.Lines);

        await RestoreAndCompareAsync(task.Id, "folder");
    }

    [Fact]
    public async Task A_zip_archive_is_written_and_restored()
    {
        var (source, destination) = Folders("zip");
        var definition = NewTask("Mac zip", source, destination);
        definition.Archive.Format = ArchiveFormats.Zip;
        var task = await service.SaveAsync(definition);

        var run = await service.RunAsync(task.Id);

        Assert.Equal(RunStatuses.Success, run.Status);
        Assert.NotEmpty(Directory.GetFiles(destination, "*.zip", SearchOption.AllDirectories));
        await RestoreAndCompareAsync(task.Id, ArchiveFormats.Zip);
    }

    /// <summary>7-Zip on macOS is 7z.so built from the sources by mac/build-7z.sh; CI puts it next to the tests.</summary>
    [Fact]
    public async Task An_encrypted_7z_archive_is_written_and_restored()
    {
        bool have7z = File.Exists(Path.Combine(AppContext.BaseDirectory, "7z.so"));
        if (!have7z && Environment.GetEnvironmentVariable("CUSTOS_REQUIRE_7Z") == "1")
        {
            Assert.Fail("7z.so is not next to the tests, and CUSTOS_REQUIRE_7Z=1 says it must be.");
        }

        Assert.SkipUnless(have7z, "7z.so is not next to the tests (build it with mac/build-7z.sh).");
        service.RequireAdmin();
        var password = await service.RequestAsync(PipeProtocol.Methods.CredentialSave,
            new CredentialInput { Credential = new Credential { Kind = CredentialKinds.ArchivePassword, User = "archive" }, Password = "Секрет-7z!" },
            IpcJsonContext.Default.CredentialInput, IpcJsonContext.Default.CredentialSaveResult);
        var (source, destination) = Folders("7z");
        var definition = NewTask("Mac 7z", source, destination);
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

    /// <summary>
    /// «Читать файлы из снимка APFS» needs root on a Mac: the run of CI with sudo covers it. A Mac where tmutil itself
    /// cannot make local snapshots (some virtual Macs, among them the runners of GitHub) checks instead what a task
    /// does then: it stops with the reason, or, set to go on, copies the files without the snapshot with a warning.
    /// </summary>
    [Fact]
    public async Task Files_are_read_from_an_APFS_snapshot()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() && Native.IsRoot, "APFS snapshots need macOS and root.");
        string? refused = SnapshotsRefused();
        var (source, destination) = Folders("snapshot");
        var definition = NewTask("Mac snapshot", source, destination);
        definition.Vss.Enabled = true;
        definition.Vss.OnFailure = VssFailureActions.Abort;
        var task = await service.SaveAsync(definition);

        var run = await service.RunAsync(task.Id);
        var log = await RunLogAsync(run.RunId);

        Assert.False(Directory.Exists(DataFolders.Snapshots) && Directory.GetDirectories(DataFolders.Snapshots).Length > 0, "A snapshot is still mounted.");
        if (refused is null)
        {
            Assert.True(run.Status == RunStatuses.Success, run.ErrorMessage);
            Assert.Contains(log, l => l.Contains("APFS", StringComparison.Ordinal));
            Assert.Single(Directory.GetFiles(destination, "a.txt", SearchOption.AllDirectories));
            return;
        }

        TestContext.Current.SendDiagnosticMessage($"This Mac makes no local snapshots ({refused}): checking what a task does then.");
        Assert.Equal(RunStatuses.Error, run.Status);
        Assert.Contains("APFS", run.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(destination, "a.txt", SearchOption.AllDirectories));

        task.Vss.OnFailure = VssFailureActions.Continue;
        task = await service.SaveAsync(task, task.Revision);
        run = await service.RunAsync(task.Id);
        log = await RunLogAsync(run.RunId);

        Assert.Equal(RunStatuses.Warning, run.Status);
        Assert.Contains(log, l => l.Contains("APFS", StringComparison.Ordinal));
        foreach (string name in Files)
        {
            string copied = Assert.Single(Directory.GetFiles(destination, Path.GetFileName(name), SearchOption.AllDirectories));
            Assert.Equal(Content(name), await File.ReadAllTextAsync(copied, TestContext.Current.CancellationToken));
        }
    }

    /// <summary>Null when tmutil can make a local snapshot on this Mac (the one made is deleted again); else what it said.</summary>
    private static string? SnapshotsRefused()
    {
        var (code, output) = Tool.Run("/usr/bin/tmutil", ["localsnapshot"], TimeSpan.FromMinutes(2));
        var made = System.Text.RegularExpressions.Regex.Match(output, @"\d{4}-\d{2}-\d{2}-\d{6}");
        if (code == 0 && made.Success)
        {
            Tool.Run("/usr/bin/tmutil", ["deletelocalsnapshots", made.Value], TimeSpan.FromMinutes(2));
            return null;
        }

        return ShadowCopies.Reason(output, "tmutil", code);
    }

    private async Task<List<string>> RunLogAsync(string runId)
    {
        var log = await service.RequestAsync(PipeProtocol.Methods.RunLog, new RunLogRequest { RunId = runId },
            IpcJsonContext.Default.RunLogRequest, IpcJsonContext.Default.RunLogResult);
        return [.. log.Lines.Select(l => l.Text)];
    }

    internal static TaskDefinition NewTask(string name, string source, string destination) => new()
    {
        Name = name + " " + Guid.NewGuid().ToString("N")[..4],
        BackupType = BackupTypes.Full,
        Sources = [new TaskSource { Kind = SourceKinds.Folder, Path = source }],
        Destinations = [new TaskDestination { Kind = DestinationKinds.Local, Path = destination }],
        Schedule = new ScheduleOptions { Type = ScheduleTypes.Manual },
        Vss = new VssOptions { Enabled = false },
    };

    internal static (string Source, string Destination) Folders(string name)
    {
        string source = TestPlace.NewFolder(name + "-src");
        foreach (string file in Files)
        {
            string path = Path.Combine(source, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Content(file));
        }

        return (source, TestPlace.NewFolder(name + "-dst"));
    }

    internal static string Content(string name) => $"CUSTOS {name}\nстрока по-русски\n";

    /// <summary>The newest copy of the task, into a folder of its own; every file comes back as it was.</summary>
    private async Task RestoreAndCompareAsync(string taskId, string storage)
    {
        if (!service.Hello.IsAdmin)
        {
            // Restoring is for administrators only (section 7).
            return;
        }

        var sets = await service.RequestAsync(PipeProtocol.Methods.RestoreList, new RestoreListRequest { TaskId = taskId },
            IpcJsonContext.Default.RestoreListRequest, IpcJsonContext.Default.RestoreListResult);
        var set = Assert.Single(sets.Sets);
        Assert.Equal(storage, set.Storage);

        string target = TestPlace.NewFolder("restored");
        await service.RequestAsync(PipeProtocol.Methods.RestoreStart,
            new RestoreStartRequest { TaskId = taskId, Location = set.Location, Where = set.Where, TargetFolder = target },
            IpcJsonContext.Default.RestoreStartRequest, IpcJsonContext.Default.OkResult);
        var status = await ServiceFixture.WaitAsync(
            () => service.RequestAsync(PipeProtocol.Methods.RestoreStatus, EmptyRequest.Instance, IpcJsonContext.Default.EmptyRequest, IpcJsonContext.Default.RestoreStatusResult),
            s => !s.Running && s.Result is not null);

        Assert.True(status.Result!.Status == RunStatuses.Success, status.Result.ErrorMessage);
        Assert.Equal(Files.Length, status.Result.FilesRestored);
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
/// The service of macOS inside the test process, and one connection to it as the window has. With
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
            int code = await _service.WaitAsync(TimeSpan.FromSeconds(40));
            Assert.Equal(0, code);
        }

        _stop.Dispose();
    }

    public Task<TResult> RequestAsync<TParams, TResult>(string method, TParams parameters, JsonTypeInfo<TParams> parametersType, JsonTypeInfo<TResult> resultType) =>
        _client!.RequestAsync(method, parameters, parametersType, resultType, CancellationToken.None).WaitAsync(Timeout);

    /// <summary>Changes need an administrator of the computer (or the setting that lets every user change tasks).</summary>
    public void RequireAdmin() => Assert.SkipUnless(Hello.IsAdmin, "Changing tasks needs an administrator of this computer.");

    /// <summary>«Сохранить» in the editor: a new task, or a change of one saved before (<paramref name="baseRevision"/>).</summary>
    public async Task<TaskDefinition> SaveAsync(TaskDefinition task, int? baseRevision = null)
    {
        RequireAdmin();
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

    private Task<AgentSnapshot> SnapshotAsync() =>
        RequestAsync(PipeProtocol.Methods.Snapshot, EmptyRequest.Instance, IpcJsonContext.Default.EmptyRequest, IpcJsonContext.Default.AgentSnapshot);
}
