using System.Text.Json;

namespace Custos.Core.Ipc;

/// <summary>
/// Sends and receives <see cref="PipeMessage"/>s over a stream. Sending is safe from several threads at once;
/// receiving is done by one reader loop.
/// </summary>
public sealed class PipeChannel : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly int _maxIncomingBytes;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <param name="stream">A connected pipe. The channel owns it and disposes it.</param>
    /// <param name="maxIncomingBytes">Largest message this side accepts.</param>
    public PipeChannel(Stream stream, int maxIncomingBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxIncomingBytes);
        _stream = stream;
        _maxIncomingBytes = maxIncomingBytes;
    }

    public async ValueTask SendAsync(PipeMessage message, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, IpcJsonContext.Default.PipeMessage);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PipeFraming.WriteFrameAsync(_stream, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Waits for the next message.</summary>
    /// <returns>The message, or null when the other side closed the pipe.</returns>
    /// <exception cref="InvalidDataException">The other side broke the protocol.</exception>
    public async ValueTask<PipeMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var payload = await PipeFraming.ReadFrameAsync(_stream, _maxIncomingBytes, cancellationToken).ConfigureAwait(false);
        if (payload is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(payload, IpcJsonContext.Default.PipeMessage)
                ?? throw new InvalidDataException("Received an empty message.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Received a malformed message: {ex.Message}", ex);
        }
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
