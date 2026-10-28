using System.Buffers.Binary;
using Custos.Core.Ipc;

namespace Custos.Core.Tests;

public sealed class PipeFramingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Frames_are_read_back_in_order_then_end_of_stream_gives_null()
    {
        using var stream = new MemoryStream();
        await PipeFraming.WriteFrameAsync(stream, "first"u8.ToArray(), Token);
        await PipeFraming.WriteFrameAsync(stream, "second frame"u8.ToArray(), Token);
        stream.Position = 0;

        Assert.Equal("first"u8.ToArray(), await PipeFraming.ReadFrameAsync(stream, 100, Token));
        Assert.Equal("second frame"u8.ToArray(), await PipeFraming.ReadFrameAsync(stream, 100, Token));
        Assert.Null(await PipeFraming.ReadFrameAsync(stream, 100, Token));
    }

    [Fact]
    public async Task Header_is_a_little_endian_length()
    {
        using var stream = new MemoryStream();
        await PipeFraming.WriteFrameAsync(stream, new byte[300], Token);

        Assert.Equal(PipeFraming.HeaderSize + 300, stream.Length);
        Assert.Equal(300, BinaryPrimitives.ReadInt32LittleEndian(stream.ToArray()));
    }

    [Fact]
    public async Task Stream_ending_inside_the_header_is_an_error()
    {
        using var stream = new MemoryStream([5, 0]);

        await Assert.ThrowsAsync<EndOfStreamException>(() => PipeFraming.ReadFrameAsync(stream, 100, Token).AsTask());
    }

    [Fact]
    public async Task Stream_ending_inside_the_payload_is_an_error()
    {
        using var stream = new MemoryStream([5, 0, 0, 0, (byte)'a', (byte)'b']);

        await Assert.ThrowsAnyAsync<EndOfStreamException>(() => PipeFraming.ReadFrameAsync(stream, 100, Token).AsTask());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task Frame_length_outside_the_limit_is_rejected_before_reading_the_payload(int length)
    {
        var header = new byte[PipeFraming.HeaderSize];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<InvalidDataException>(() => PipeFraming.ReadFrameAsync(stream, 100, Token).AsTask());
    }

    [Fact]
    public async Task Empty_frame_cannot_be_written()
    {
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentException>(() => PipeFraming.WriteFrameAsync(stream, ReadOnlyMemory<byte>.Empty, Token).AsTask());
    }
}
