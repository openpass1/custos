using System.Text;
using System.Text.Json;
using Custos.Core.Ipc;

namespace Custos.Core.Tests;

public sealed class PipeMessageTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Request_is_snake_case_json_without_null_fields()
    {
        var request = PipeMessage.Request(
            7,
            PipeProtocol.Methods.Hello,
            new HelloRequest { Protocol = 1, ClientVersion = "0.1.0" },
            IpcJsonContext.Default.HelloRequest);

        string json = JsonSerializer.Serialize(request, IpcJsonContext.Default.PipeMessage);

        Assert.Equal("""{"kind":"request","id":7,"method":"hello","data":{"protocol":1,"client_version":"0.1.0"}}""", json);
    }

    [Fact]
    public void Event_payload_survives_a_round_trip()
    {
        var sent = PipeMessage.Notification(
            PipeProtocol.Events.ServiceState,
            new ServiceStateEvent { State = PipeProtocol.ServiceStates.Stopping },
            IpcJsonContext.Default.ServiceStateEvent);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(sent, IpcJsonContext.Default.PipeMessage);
        var received = JsonSerializer.Deserialize(bytes, IpcJsonContext.Default.PipeMessage)!;

        Assert.Equal(PipeMessageKind.Event, received.Kind);
        Assert.Null(received.Id);
        Assert.Equal(PipeProtocol.Events.ServiceState, received.Event);
        Assert.Equal(PipeProtocol.ServiceStates.Stopping, received.GetData(IpcJsonContext.Default.ServiceStateEvent).State);
    }

    [Fact]
    public void Failure_carries_code_and_message_but_no_data()
    {
        var failure = PipeMessage.Failure(3, PipeProtocol.Errors.UnknownMethod, "Unknown method x.");

        string json = JsonSerializer.Serialize(failure, IpcJsonContext.Default.PipeMessage);

        Assert.Equal("""{"kind":"response","id":3,"error":{"code":"unknown_method","message":"Unknown method x."}}""", json);
    }

    [Fact]
    public void Missing_data_is_reported_as_invalid_data()
    {
        var response = new PipeMessage { Kind = PipeMessageKind.Response, Id = 1 };

        Assert.Throws<InvalidDataException>(() => response.GetData(IpcJsonContext.Default.HelloResult));
    }

    [Fact]
    public void Data_of_the_wrong_shape_is_reported_as_invalid_data()
    {
        var response = PipeMessage.Success(1, new ServiceStateEvent { State = "running" }, IpcJsonContext.Default.ServiceStateEvent);

        Assert.Throws<InvalidDataException>(() => response.GetData(IpcJsonContext.Default.HelloResult));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"id":1}""")]
    [InlineData("null")]
    public async Task Channel_rejects_malformed_messages(string payload)
    {
        using var stream = new MemoryStream();
        await PipeFraming.WriteFrameAsync(stream, Encoding.UTF8.GetBytes(payload), Token);
        stream.Position = 0;
        await using var channel = new PipeChannel(stream, 1024);

        await Assert.ThrowsAsync<InvalidDataException>(() => channel.ReceiveAsync(Token).AsTask());
    }

    [Fact]
    public async Task Channel_sends_and_receives_messages()
    {
        var stream = new MemoryStream();
        await using var channel = new PipeChannel(stream, 1024);
        await channel.SendAsync(PipeMessage.Failure(1, "a", "b"), Token);
        await channel.SendAsync(PipeMessage.Failure(2, "c", "d"), Token);
        stream.Position = 0;

        Assert.Equal(1, (await channel.ReceiveAsync(Token))!.Id);
        Assert.Equal("c", (await channel.ReceiveAsync(Token))!.Error!.Code);
        Assert.Null(await channel.ReceiveAsync(Token));
    }
}
