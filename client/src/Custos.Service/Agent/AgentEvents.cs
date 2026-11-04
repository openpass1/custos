using System.Threading.Channels;
using Custos.Core.Ipc;

namespace Custos.Service.Agent;

/// <summary>
/// Events for the interfaces, in the order they happened. The agent writes, the pipe server reads and sends them to
/// every connected interface.
/// </summary>
internal sealed class AgentEvents
{
    private readonly Channel<PipeMessage> _channel = Channel.CreateBounded<PipeMessage>(new BoundedChannelOptions(1000)
    {
        // An interface that cannot keep up loses old progress rather than holding up the backups.
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });

    public ChannelReader<PipeMessage> Reader => _channel.Reader;

    public void Publish(PipeMessage message) => _channel.Writer.TryWrite(message);
}
