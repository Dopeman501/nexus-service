using System;
using Nexus.Service.Peripherals.JungleLeopard;

namespace Nexus.Service.Tests.JungleLeopard;

public class D215ProtocolTests
{
    // Captured firmware 3.4 request and response bytes are recorded in
    // JUNGLE_LEOPARD_DEVICE_PROTOCOL.md.
    [Fact]
    public void BuildInfoRequest_matches_the_wire_vector()
    {
        Assert.Equal(Convert.FromHexString("55AA0700060C01"), D215Protocol.BuildInfoRequest());
    }

    [Fact]
    public void BuildLiveModeRequest_matches_the_wire_vector()
    {
        Assert.Equal(Convert.FromHexString("55AA0700111701"), D215Protocol.BuildLiveModeRequest());
    }

    [Theory]
    // Lower presets use the same documented one-byte payload/checksum format;
    // these vectors do not claim physical verification of those levels.
    [InlineData(20, "55AA080003141E01")]
    [InlineData(40, "55AA080003283201")]
    [InlineData(60, "55AA0800033C4601")]
    [InlineData(80, "55AA080003505A01")]
    [InlineData(100, "55AA080003646E01")]
    public void BuildBrightnessRequest_matches_the_preset_wire_vectors(int brightness, string expected)
    {
        Assert.Equal(Convert.FromHexString(expected), D215Protocol.BuildBrightnessRequest(brightness));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(19)]
    [InlineData(21)]
    [InlineData(39)]
    [InlineData(41)]
    [InlineData(59)]
    [InlineData(61)]
    [InlineData(79)]
    [InlineData(81)]
    [InlineData(99)]
    [InlineData(101)]
    [InlineData(336)]
    public void BuildBrightnessRequest_rejects_values_outside_the_presets(int brightness)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => D215Protocol.BuildBrightnessRequest(brightness));
    }

    [Fact]
    public void BuildJpegEnvelope_writes_little_endian_length_payload_and_checksum()
    {
        var jpeg = Convert.FromHexString("FFD8FFD9");

        // Four payload bytes and a sum of 947 (0x03B3), including the length.
        var expected = Convert.FromHexString("04000000FFD8FFD9B303");
        Assert.Equal(expected, D215Protocol.BuildJpegEnvelope(jpeg));
    }

    [Fact]
    public void BuildJpegEnvelope_accepts_the_payload_ceiling_and_wraps_the_checksum()
    {
        var jpeg = new byte[305 * 1024];
        Array.Fill(jpeg, (byte)0xFF);

        var envelope = D215Protocol.BuildJpegEnvelope(jpeg);

        Assert.Equal(jpeg.Length + 6, envelope.Length);
        Assert.Equal(Convert.FromHexString("00C40400"), envelope[..4]);
        Assert.True(jpeg.AsSpan().SequenceEqual(envelope.AsSpan(4, jpeg.Length)));
        // 312320 * 255 + 0xC4 + 0x04, reduced modulo 65536, is 0x3CC8.
        Assert.Equal(Convert.FromHexString("C83C"), envelope[^2..]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(305 * 1024 + 1)]
    public void BuildJpegEnvelope_rejects_payloads_outside_its_size_bound(int length)
    {
        var jpeg = new byte[length];

        Assert.Throws<ArgumentOutOfRangeException>(() => D215Protocol.BuildJpegEnvelope(jpeg));
    }

    [Fact]
    public void TryReadControlFrame_extracts_the_live_mode_acknowledgement()
    {
        var reply = Convert.FromHexString("55AA080011001801");

        Assert.True(D215Protocol.TryReadControlFrame(reply, out var command, out var payload));
        Assert.Equal(0x11, command);
        Assert.Equal(new byte[] { 0x00 }, payload.ToArray());
    }

    [Fact]
    public void TryReadControlFrame_accepts_the_minimum_frame_with_an_empty_payload()
    {
        var frame = Convert.FromHexString("55AA0700222801");

        Assert.True(D215Protocol.TryReadControlFrame(frame, out var command, out var payload));
        Assert.Equal(0x22, command);
        Assert.True(payload.IsEmpty);
    }

    [Theory]
    [InlineData("")]
    [InlineData("55AA06000501")] // Matching checksum with no command byte.
    [InlineData("55AA0800110018")] // Truncated acknowledgement.
    [InlineData("55AA08001100180100")] // Trailing byte.
    [InlineData("54AA080011001701")] // Invalid first header byte, matching checksum.
    [InlineData("55AB080011001901")] // Invalid second header byte, matching checksum.
    [InlineData("55AA090011001901")] // Wrong advertised length, matching checksum.
    [InlineData("55AA080011001901")] // Incorrect checksum.
    public void TryReadControlFrame_rejects_invalid_frames(string hex)
    {
        Assert.False(D215Protocol.TryReadControlFrame(Convert.FromHexString(hex), out _, out _));
    }

    [Theory]
    [InlineData(4096, true)]
    [InlineData(4097, false)]
    public void TryReadControlFrame_enforces_the_total_frame_bound(int length, bool expected)
    {
        var reply = new byte[length];
        reply[0] = 0x55;
        reply[1] = 0xAA;
        reply[2] = (byte)(length - 4096);
        reply[3] = 0x10;
        reply[4] = 0x22;
        // All payload bytes are zero. For a 4096-byte frame the sum is 0x0131.
        reply[^2] = (byte)(0x31 + length - 4096);
        reply[^1] = 0x01;

        Assert.Equal(expected, D215Protocol.TryReadControlFrame(reply, out _, out _));
    }
}
