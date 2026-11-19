using Custos.Core.Ipc;
using Custos.Core.Model;

namespace Custos.Mac.Tests;

/// <summary>
/// The service that Custos-&lt;version&gt;.pkg installed on this Mac, started by launchd as root: it answers the window
/// over /var/run/custos.sock, keeps a task, runs it and writes the copy. CI runs these after the installer
/// (CUSTOS_INSTALLED_SERVICE=1, CUSTOS_EXPECTED_VERSION=the version of the package); without the variable they skip.
/// </summary>
[Collection(ServiceGroup.Name)]
public sealed class InstalledServiceTests(ServiceFixture service)
{
    [Fact]
    public void The_installed_service_answers_as_root_with_its_version()
    {
        Assert.SkipUnless(TestPlace.Installed, "Set CUSTOS_INSTALLED_SERVICE=1 after installing Custos-<version>.pkg.");
        Assert.Equal(PipeProtocol.Version, service.Hello.Protocol);
        if (Environment.GetEnvironmentVariable("CUSTOS_EXPECTED_VERSION") is { Length: > 0 } version)
        {
            Assert.Equal(version, service.Hello.ServiceVersion);
        }

        Assert.Equal(Environment.UserName, service.Hello.User);

        // The user of the GitHub runner is an administrator of the Mac, so the window may change tasks.
        Assert.True(service.Hello.IsAdmin, "An administrator of this Mac must be one for the service too.");
    }

    [Fact]
    public async Task The_installed_service_runs_a_task_and_writes_the_copy()
    {
        Assert.SkipUnless(TestPlace.Installed, "Set CUSTOS_INSTALLED_SERVICE=1 after installing Custos-<version>.pkg.");
        var (source, destination) = ServiceTests.Folders("installed");
        var task = await service.SaveAsync(ServiceTests.NewTask("Installed copy", source, destination));
        try
        {
            var run = await service.RunAsync(task.Id);

            Assert.True(run.Status == RunStatuses.Success, run.ErrorMessage);
            foreach (string name in ServiceTests.Files)
            {
                string copied = Assert.Single(Directory.GetFiles(destination, Path.GetFileName(name), SearchOption.AllDirectories));
                Assert.Equal(ServiceTests.Content(name), await File.ReadAllTextAsync(copied, TestContext.Current.CancellationToken));
            }
        }
        finally
        {
            // The installed service keeps its tasks; this one is only for the test.
            await service.RequestAsync(PipeProtocol.Methods.TaskDelete, new TaskRequest { TaskId = task.Id },
                IpcJsonContext.Default.TaskRequest, IpcJsonContext.Default.OkResult);
        }
    }
}
