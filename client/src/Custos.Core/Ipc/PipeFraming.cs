using System.Buffers.Binary;

namespace Custos.Core.Ipc;

/// <summary>Length-prefixed frames: a 4-byte little-endian length, then the payload.</summary>
public static class PipeFraming
{
    public const int HeaderSize = 4;

    public static async ValueTask WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (payload.IsEmpty)
        {
            throw new ArgumentException("A frame cannot be empty.", nameof(payload));
        }

        // One write per frame, so that a frame is never interleaved with another writer's data.
        var buffer = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, payload.Length);
        payload.CopyTo(buffer.AsMemory(HeaderSize));
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one frame.</summary>
    /// <returns>The payload, or null if the stream ended cleanly before a new frame.</returns>
    /// <exception cref="EndOfStreamException">The stream ended in the middle of a frame.</exception>
    /// <exception cref="InvalidDataException">The frame length is zero, negative or above <paramref name="maxLength"/>.</exception>
    public static async ValueTask<byte[]?> ReadFrameAsync(Stream stream, int maxLength, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[HeaderSize];
        int read = await stream.ReadAtLeastAsync(header, HeaderSize, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        if (read < HeaderSize)
        {
            throw new EndOfStreamException("The pipe closed in the middle of a frame header.");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > maxLength)
        {
            throw new InvalidDataException($"Invalid frame length {length} (allowed 1 to {maxLength} bytes).");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }
}
