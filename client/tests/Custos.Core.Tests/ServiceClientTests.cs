using System.IO.Pipes;
using Custos.Core.Ipc;

namespace Custos.Core.Tests;

/// <summary>The interface's client against a small test server over a real named pipe.</summary>
public sealed class ServiceClientTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Hello_is_answered_and_events_reach_the_handler()
    {
        string pipeName = NewPipeName();
        var events = new TaskCompletionSource<PipeMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = ServeOneClientAsync(pipeName, async (channel, request) =>
        {
            var hello = request.GetData(IpcJsonContext.Default.HelloRequest);
            await channel.SendAsync(PipeMessage.Success(request.Id!.Value, NewHelloResult(hello.ClientVersion), IpcJsonContext.Default.HelloResult), Token);
            await channel.SendAsync(PipeMessage.Notification(
                PipeProtocol.Events.ServiceState,
                new ServiceStateEvent { State = PipeProtocol.ServiceStates.Stopping },
                IpcJsonContext.Default.ServiceStateEvent), Token);
        });

        await using var client = await ServiceClient.ConnectAsync(events.SetResult, Timeout, Token, pipeName);
        var result = await SayHelloAsync(client);
        var stateEvent = await events.Task.WaitAsync(Timeout, Token);

        Assert.Equal(PipeProtocol.Version, result.Protocol);
        Assert.Equal("test-client", result.ServiceVersion);
        Assert.Equal(PipeProtocol.ServiceStates.Stopping, stateEvent.GetData(IpcJsonContext.Default.ServiceStateEvent).State);
        await server.WaitAsync(Timeout, Token);
    }

    [Fact]
    public async Task Error_response_becomes_ServiceRequestException_with_its_code()
    {
        string pipeName = NewPipeName();
        var server = ServeOneClientAsync(pipeName, (channel, request) =>
            channel.SendAsync(PipeMessage.Failure(request.Id!.Value, PipeProtocol.Errors.ProtocolMismatch, "different versions"), Token).AsTask());

        await using var client = await ServiceClient.ConnectAsync(null, Timeout, Token, pipeName);
        var error = await Assert.ThrowsAsync<ServiceRequestException>(() => SayHelloAsync(client));

        Assert.Equal(PipeProtocol.Errors.ProtocolMismatch, error.Code);
        await server.WaitAsync(Timeout, Token);
    }

    [Fact]
    public async Task Server_closing_the_pipe_completes_the_connection_and_fails_the_waiting_request()
    {
        string pipeName = NewPipeName();
        var server = ServeOneClientAsync(pipeName, (_, _) => Task.CompletedTask);

        await using var client = await ServiceClient.ConnectAsync(null, Timeout, Token, pipeName);
        await Assert.ThrowsAsync<IOException>(() => SayHelloAsync(client));
        await client.Completion.WaitAsync(Timeout, Token);

        await Assert.ThrowsAsync<IOException>(() => SayHelloAsync(client));
        await server.WaitAsync(Timeout, Token);
    }

    [Fact]
    public async Task Connecting_without_a_server_times_out()
    {
        await Assert.ThrowsAsync<TimeoutException>(() =>
            ServiceClient.ConnectAsync(null, TimeSpan.FromMilliseconds(300), Token, NewPipeName()));
    }

    /// <summary>
    /// A pipe of its own for each test. Outside Windows a pipe is a Unix socket in the temporary folder, and on macOS
    /// that folder is so long that the path passes the 104 bytes a socket may have: there the socket goes into /tmp.
    /// </summary>
    internal static string NewPipeName() => OperatingSystem.IsWindows()
        ? "custos-test-" + Guid.NewGuid().ToString("N")
        : "/tmp/custos-test-" + Guid.NewGuid().ToString("N")[..12];

    internal static Task<HelloResult> SayHelloAsync(ServiceClient client) =>
        client.RequestAsync(
            PipeProtocol.Methods.Hello,
            new HelloRequest { Protocol = PipeProtocol.Version, ClientVersion = "test-client" },
            IpcJsonContext.Default.HelloRequest,
            IpcJsonContext.Default.HelloResult,
            Token).WaitAsync(Timeout, Token);

    internal static HelloResult NewHelloResult(string version) => new()
    {
        Protocol = PipeProtocol.Version,
        ServiceVersion = version,
        State = PipeProtocol.ServiceStates.Running,
        StartedAt = DateTimeOffset.Now,
    };

    /// <summary>Accepts one client, hands its first request to <paramref name="respond"/>, then closes the pipe.</summary>
    internal static Task ServeOneClientAsync(string pipeName, Func<PipeChannel, PipeMessage, Task> respond)
    {
        var pipe = OperatingSystem.IsWindows()
            ? ServicePipe.CreateServer(pipeName, firstInstance: true)
            : new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        return Task.Run(async () =>
        {
            await using var channel = new PipeChannel(pipe, PipeProtocol.MaxRequestBytes);
            await pipe.WaitForConnectionAsync(Token);
            var request = await channel.ReceiveAsync(Token);
            Assert.NotNull(request);
            Assert.Equal(PipeMessageKind.Request, request.Kind);
            await respond(channel, request);
        }, Token);
    }
}
