using System;

namespace Nexus.Service.Peripherals.JungleLeopard;

internal sealed class D215ControlReader
{
    internal const int MaximumAppendBytes = 512;

    // Retain one incomplete frame and enough space for the next serial read.
    private readonly byte[] _buffer = new byte[D215Protocol.MaximumControlFrameBytes - 1 + MaximumAppendBytes];
    private int _start;
    private int _end;

    internal void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumAppendBytes)
            throw new ArgumentOutOfRangeException(nameof(bytes));

        if (bytes.Length > _buffer.Length - _end)
        {
            var pending = _buffer.AsSpan(_start, _end - _start);
            if (bytes.Length > _buffer.Length - pending.Length)
                throw new InvalidOperationException("Drain complete control frames before appending more serial bytes.");

            pending.CopyTo(_buffer);
            _end = pending.Length;
            _start = 0;
        }

        bytes.CopyTo(_buffer.AsSpan(_end));
        _end += bytes.Length;
    }

    internal bool TryRead(out byte command, out byte[] payload)
    {
        command = default;
        payload = [];
        while (_start < _end)
        {
            var pending = _buffer.AsSpan(_start, _end - _start);
            var header = pending.IndexOf(D215Protocol.ControlSync);
            if (header < 0)
            {
                // A sync prefix may have arrived at the very end of this read.
                Consume(pending.Length - (pending[^1] == D215Protocol.HeaderFirst ? 1 : 0));
                break;
            }

            Consume(header);
            pending = pending[header..];
            if (pending.Length < D215Protocol.ControlPrefixBytes)
                break;

            var length = D215Protocol.ReadControlLength(pending);
            if (length is < D215Protocol.MinimumControlFrameBytes or > D215Protocol.MaximumControlFrameBytes)
            {
                Consume(1);
                continue;
            }

            // An incomplete, plausible length stays pending until more bytes or the request deadline.
            if (pending.Length < length)
                break;

            if (!D215Protocol.TryReadControlFrame(pending[..length], out command, out var framePayload))
            {
                Consume(1);
                continue;
            }

            payload = framePayload.ToArray();
            Consume(length);
            return true;
        }

        return false;
    }

    private void Consume(int bytes)
    {
        _start += bytes;
        if (_start == _end)
        {
            _start = 0;
            _end = 0;
        }
    }
}
