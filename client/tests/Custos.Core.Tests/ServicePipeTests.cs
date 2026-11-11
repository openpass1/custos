using System.Runtime.Versioning;
using Custos.Core.Ipc;

namespace Custos.Core.Tests;

/// <summary>Access rules of the service pipe. Windows only; skipped elsewhere.</summary>
public sealed class ServicePipeTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Client_gets_in_with_the_rights_the_pipe_grants_to_ordinary_users()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Pipe access rules exist only on Windows.");
        string pipeName = ServiceClientTests.NewPipeName();
        var server = ServiceClientTests.ServeOneClientAsync(pipeName, (channel, request) =>
            channel.SendAsync(
                PipeMessage.Success(request.Id!.Value, ServiceClientTests.NewHelloResult("acl"), IpcJsonContext.Default.HelloResult),
                TestContext.Current.CancellationToken).AsTask());

        await using var client = await ServiceClient.ConnectAsync(null, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken, pipeName);
        var result = await ServiceClientTests.SayHelloAsync(client);

        Assert.Equal("acl", result.ServiceVersion);
        await server.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Service_can_add_instances_but_cannot_be_first_twice()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Pipe access rules exist only on Windows.");
        string pipeName = ServiceClientTests.NewPipeName();
        using var first = ServicePipe.CreateServer(pipeName, firstInstance: true);

        using var second = ServicePipe.CreateServer(pipeName, firstInstance: false);
        Assert.Throws<UnauthorizedAccessException>(() => ServicePipe.CreateServer(pipeName, firstInstance: true));
    }
}
