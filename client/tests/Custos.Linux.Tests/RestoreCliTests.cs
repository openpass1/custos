using Custos.Core.Ipc;
using Custos.Core.Model;
using Custos.Linux.Cli;
using Custos.Service.Platform;

namespace Custos.Linux.Tests;

[Collection(ServiceGroup.Name)]
public sealed class RestoreCliTests(ServiceFixture service)
{
    [Fact]
    public async Task The_cli_restores_an_existing_zip_from_a_folder_and_leaves_existing_files_alone()
    {
        var (source, destination) = ServiceTests.Folders("cli-restore");
        var definition = ServiceTests.NewTask("CLI restore", source, destination);
        definition.Archive.Format = ArchiveFormats.Zip;
        var task = await service.SaveAsync(definition);
        var run = await service.RunAsync(task.Id);
        Assert.Equal(RunStatuses.Success, run.Status);

        Assert.Equal(0, await Commands.RunAsync(["restore", "list", "--task", task.Name]));
        Assert.Equal(0, await Commands.RunAsync(["restore", "list", "--from", destination]));

        // No default write location. Explicitly restoring to the originals leaves a newer file alone.
        string existing = Path.Combine(source, "a.txt");
        string missing = Path.Combine(source, "sub/b.txt");
        File.WriteAllText(existing, "current contents");
        File.Delete(missing);
        Assert.Equal(1, await Commands.RunAsync(["restore", "start", "--from", destination, "--set", run.RunId]));
        Assert.False(File.Exists(missing));
        Assert.Equal(0, await Commands.RunAsync(["restore", "start", "--from", destination, "--set", run.RunId, "--original"]));
        Assert.Equal("current contents", File.ReadAllText(existing));
        Assert.Equal(ServiceTests.Content("sub/b.txt"), File.ReadAllText(missing));

        // Two task destinations can have the same visible label, even though they have different ids.
        task.Destinations.Add(new TaskDestination { Path = destination });
        task = await service.SaveAsync(task, task.Revision);
        var list = await service.RequestAsync(PipeProtocol.Methods.RestoreList, new RestoreListRequest { TaskId = task.Id },
            IpcJsonContext.Default.RestoreListRequest, IpcJsonContext.Default.RestoreListResult);
        Assert.Equal(2, list.Sets.Count);
        Assert.Equal(list.Sets[0].Where, list.Sets[1].Where);
        Assert.NotEqual(list.Sets[0].DestinationId, list.Sets[1].DestinationId);

        string target = TestPlace.NewFolder("cli-restored");
        Assert.Equal(1, await Commands.RunAsync(["restore", "start", "--task", task.Id, "--set", run.RunId, "--to", target]));
        Assert.Empty(Directory.GetFileSystemEntries(target));
        Assert.Equal(0, await Commands.RunAsync(["restore", "start", "--task", task.Id, "--set", run.RunId,
            "--destination", task.Destinations[1].Id, "--to", target]));
        Assert.Equal(0, await Commands.RunAsync(["restore", "status"]));
        Assert.Equal(1, await Commands.RunAsync(["restore", "stop"]));
        foreach (string name in ServiceTests.Files)
        {
            string found = Assert.Single(Directory.GetFiles(target, Path.GetFileName(name), SearchOption.AllDirectories));
            Assert.Equal(ServiceTests.Content(name), File.ReadAllText(found));
        }
    }

    [Fact]
    public async Task A_stop_for_another_job_leaves_the_active_restore_running()
    {
        var (source, destination) = ServiceTests.Folders("cli-stop");
        var task = await service.SaveAsync(ServiceTests.NewTask("CLI stop", source, destination));
        var run = await service.RunAsync(task.Id);
        Assert.Equal(RunStatuses.Success, run.Status);

        var list = await service.RequestAsync(PipeProtocol.Methods.RestoreList, new RestoreListRequest { TaskId = task.Id },
            IpcJsonContext.Default.RestoreListRequest, IpcJsonContext.Default.RestoreListResult);
        var set = Assert.Single(list.Sets);
        string first = Assert.Single(Directory.GetFiles(destination, "a.txt", SearchOption.AllDirectories));
        File.Delete(first);
        var made = Tool.Run("mkfifo", [first]);
        Assert.True(made.ExitCode == 0, made.Output);

        Task<RestoreStatusResult> Status() => service.RequestAsync(PipeProtocol.Methods.RestoreStatus, EmptyRequest.Instance,
            IpcJsonContext.Default.EmptyRequest, IpcJsonContext.Default.RestoreStatusResult);
        Task<OkResult> Stop(string jobId) => service.RequestAsync(PipeProtocol.Methods.RestoreStop, new RestoreStopRequest { JobId = jobId },
            IpcJsonContext.Default.RestoreStopRequest, IpcJsonContext.Default.OkResult);

        // Keep the copied file blocked until both stop requests have reached the service.
        using (var gate = new FileStream(first, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var started = await service.RequestAsync(PipeProtocol.Methods.RestoreStart,
                new RestoreStartRequest { TaskId = task.Id, Location = set.Location, TargetFolder = TestPlace.NewFolder("cli-stopped") },
                IpcJsonContext.Default.RestoreStartRequest, IpcJsonContext.Default.RestoreStartResult);
            Assert.False((await Stop(started.JobId + "-old")).Ok);
            var running = await Status();
            Assert.Equal(started.JobId, running.JobId);
            Assert.True(running.Running);

            Assert.True((await Stop(started.JobId)).Ok);
            // Wake the reader so the copy loop can observe cancellation.
            gate.WriteByte(0);
        }

        var finished = await ServiceFixture.WaitAsync(Status, status => !status.Running);
        Assert.Equal(RunStatuses.Aborted, finished.Result?.Status);
    }

    [Fact]
    public async Task A_non_administrator_cannot_list_start_read_or_stop_a_restore()
    {
        Assert.SkipWhen(service.Hello.IsAdmin, "This test must run without administrator rights.");
        string folder = TestPlace.NewFolder("cli-restore-denied");
        Assert.Equal(1, await Commands.RunAsync(["restore", "list", "--from", folder]));
        Assert.Equal(1, await Commands.RunAsync(["restore", "start", "--from", folder, "--set", "any-set", "--to", folder]));
        Assert.Equal(1, await Commands.RunAsync(["restore", "status"]));
        Assert.Equal(1, await Commands.RunAsync(["restore", "stop"]));

        var deniedStart = await Assert.ThrowsAsync<ServiceRequestException>(() => service.RequestAsync(PipeProtocol.Methods.RestoreStart,
            new RestoreStartRequest { Folder = folder, Location = "any-set", TargetFolder = folder },
            IpcJsonContext.Default.RestoreStartRequest, IpcJsonContext.Default.RestoreStartResult));
        Assert.Equal(PipeProtocol.Errors.Forbidden, deniedStart.Code);
        var deniedStop = await Assert.ThrowsAsync<ServiceRequestException>(() => service.RequestAsync(PipeProtocol.Methods.RestoreStop,
            EmptyRequest.Instance, IpcJsonContext.Default.EmptyRequest, IpcJsonContext.Default.OkResult));
        Assert.Equal(PipeProtocol.Errors.Forbidden, deniedStop.Code);
    }
}
