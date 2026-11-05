using System;
using Nexus.Service.Peripherals.JungleLeopard;

namespace Nexus.Service.Tests.JungleLeopard;

public sealed class D215ControlReaderTests
{
    private static byte[] LiveAcknowledgement() => Convert.FromHexString("55AA080011001801");

    [Fact]
    public void TryRead_preserves_partial_bytes_across_every_boundary()
    {
        var frame = LiveAcknowledgement();
        var reader = new D215ControlReader();

        for (var index = 0; index < frame.Length - 1; index++)
        {
            reader.Append(frame.AsSpan(index, 1));
            Assert.False(reader.TryRead(out _, out _));
        }

        reader.Append(frame.AsSpan(frame.Length - 1));
        Assert.True(reader.TryRead(out var command, out var payload));
        Assert.Equal(0x11, command);
        Assert.Equal(new byte[] { 0 }, payload);
        Assert.False(reader.TryRead(out _, out _));
    }

    [Fact]
    public void TryRead_skips_garbage_and_keeps_coalesced_frames_in_wire_order()
    {
        var reader = new D215ControlReader();
        reader.Append(Convert.FromHexString("43550055"));
        Assert.False(reader.TryRead(out _, out _));

        // A header split across reads, followed by the observed live acknowledgement.
        reader.Append(Convert.FromHexString("AA080022012A0155AA080011001801"));

        Assert.True(reader.TryRead(out var firstCommand, out var firstPayload));
        Assert.Equal(0x22, firstCommand);
        Assert.True(reader.TryRead(out var secondCommand, out var secondPayload));
        Assert.Equal(0x11, secondCommand);
        Assert.Equal(new byte[] { 0 }, secondPayload);
        Assert.Equal(new byte[] { 1 }, firstPayload);
        Assert.False(reader.TryRead(out _, out _));
    }

    [Theory]
    [InlineData("55AA06000501")] // Too short, with a valid checksum.
    [InlineData("55AA0110001001")] // Declares 4097 bytes.
    [InlineData("55AA080011001901")] // Bad checksum.
    public void TryRead_resynchronizes_past_malformed_frames(string malformed)
    {
        var reader = new D215ControlReader();
        reader.Append(Convert.FromHexString(malformed + "55AA080011001801"));

        Assert.True(reader.TryRead(out var command, out var payload));
        Assert.Equal(0x11, command);
        Assert.Equal(new byte[] { 0 }, payload);
        Assert.False(reader.TryRead(out _, out _));
    }

    [Fact]
    public void TryRead_handles_a_maximum_frame_and_the_next_reply_in_one_read()
    {
        var frame = new byte[4096];
        Convert.FromHexString("55AA001022").CopyTo(frame, 0);
        frame[^2] = 0x31;
        frame[^1] = 0x01;
        var reader = new D215ControlReader();
        var acknowledgement = LiveAcknowledgement();
        var firstChunk = new byte[512];
        acknowledgement.CopyTo(firstChunk, 0);
        frame.AsSpan(0, 504).CopyTo(firstChunk.AsSpan(8));
        reader.Append(firstChunk);
        Assert.True(reader.TryRead(out var previous, out _));
        Assert.Equal(0x11, previous);
        Assert.False(reader.TryRead(out _, out _));

        for (var index = 504; index < 4095; index += 512)
        {
            reader.Append(frame.AsSpan(index, Math.Min(512, 4095 - index)));
            Assert.False(reader.TryRead(out _, out _));
        }

        var finalChunk = new byte[512];
        finalChunk[0] = 1;
        for (var offset = 1; offset <= finalChunk.Length - 8; offset += 8)
            acknowledgement.CopyTo(finalChunk, offset);

        reader.Append(finalChunk);
        Assert.True(reader.TryRead(out var first, out var payload));
        Assert.Equal(0x22, first);
        Assert.Equal(4089, payload.Length);
        for (var index = 0; index < 63; index++)
        {
            Assert.True(reader.TryRead(out var next, out _));
            Assert.Equal(0x11, next);
        }
        Assert.False(reader.TryRead(out _, out _));
    }

    [Fact]
    public void Append_rejects_a_chunk_larger_than_the_transport_read_size()
    {
        var reader = new D215ControlReader();

        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Append(new byte[513]));
    }

    [Fact]
    public void Append_rejects_undrained_input_when_the_buffer_is_full()
    {
        var reader = new D215ControlReader();
        var chunk = new byte[512];
        for (var index = 0; index < 8; index++)
            reader.Append(chunk);

        Assert.Throws<InvalidOperationException>(() => reader.Append(chunk));
    }
}
