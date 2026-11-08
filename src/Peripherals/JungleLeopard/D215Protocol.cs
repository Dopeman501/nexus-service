using System;
using System.Buffers.Binary;

namespace Nexus.Service.Peripherals.JungleLeopard;

internal static class D215Protocol
{
    internal const byte HeaderFirst = 0x55;
    private const byte HeaderSecond = 0xAA;
    internal const byte InfoCommand = 0x06;
    internal const byte BrightnessCommand = 0x03;
    internal const byte LiveModeCommand = 0x11;
    internal const byte RestartCommand = 0x01;
    private const int ControlHeaderBytes = 5;
    private const int ChecksumBytes = sizeof(ushort);
    internal const int ControlPrefixBytes = ControlHeaderBytes - 1;
    internal const int MinimumControlFrameBytes = ControlHeaderBytes + ChecksumBytes;
    internal const int MaximumControlFrameBytes = 4 * 1024;
    internal const int MaximumControlPayloadBytes = MaximumControlFrameBytes - MinimumControlFrameBytes;
    private const int JpegLengthBytes = sizeof(uint);
    // The vendor profile supplies this provisional rejection ceiling.
    internal const int MaximumJpegBytes = 305 * 1024;

    internal static ReadOnlySpan<byte> ControlSync => [HeaderFirst, HeaderSecond];

    internal static byte[] BuildInfoRequest() => BuildControlRequest(InfoCommand, []);

    internal static byte[] BuildLiveModeRequest() => BuildControlRequest(LiveModeCommand, []);

    internal static byte[] BuildRestartRequest() => BuildControlRequest(RestartCommand, []);

    internal static byte[] BuildBrightnessRequest(int brightness)
    {
        if (brightness is not (20 or 40 or 60 or 80 or 100))
            throw new ArgumentOutOfRangeException(nameof(brightness), brightness, "D215 brightness must be 20, 40, 60, 80 or 100.");

        return BuildControlRequest(BrightnessCommand, [(byte)brightness]);
    }

    internal static byte[] BuildJpegEnvelope(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.IsEmpty || jpeg.Length > MaximumJpegBytes)
            throw new ArgumentOutOfRangeException(nameof(jpeg), "The JPEG payload must be between 1 byte and 305 KiB.");

        var envelope = new byte[JpegLengthBytes + jpeg.Length + ChecksumBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(envelope, (uint)jpeg.Length);
        jpeg.CopyTo(envelope.AsSpan(JpegLengthBytes));
        WriteChecksum(envelope);
        return envelope;
    }

    internal static bool TryReadControlFrame(ReadOnlySpan<byte> frame, out byte command, out ReadOnlySpan<byte> payload)
    {
        command = default;
        payload = default;
        if (frame.Length is < MinimumControlFrameBytes or > MaximumControlFrameBytes
            || frame[0] != HeaderFirst || frame[1] != HeaderSecond
            || ReadControlLength(frame) != frame.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(frame[^ChecksumBytes..]) != Sum16(frame[..^ChecksumBytes]))
        {
            return false;
        }

        command = frame[4];
        payload = frame[ControlHeaderBytes..^ChecksumBytes];
        return true;
    }

    internal static int ReadControlLength(ReadOnlySpan<byte> prefix)
        => BinaryPrimitives.ReadUInt16LittleEndian(prefix[2..]);

    private static byte[] BuildControlRequest(byte command, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[MinimumControlFrameBytes + payload.Length];
        frame[0] = HeaderFirst;
        frame[1] = HeaderSecond;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), (ushort)frame.Length);
        frame[4] = command;
        payload.CopyTo(frame.AsSpan(ControlHeaderBytes));
        WriteChecksum(frame);
        return frame;
    }

    private static void WriteChecksum(Span<byte> frame)
        => BinaryPrimitives.WriteUInt16LittleEndian(frame[^ChecksumBytes..], Sum16(frame[..^ChecksumBytes]));

    private static ushort Sum16(ReadOnlySpan<byte> bytes)
    {
        int sum = 0;
        foreach (byte value in bytes)
            sum += value;
        return unchecked((ushort)sum);
    }
}
