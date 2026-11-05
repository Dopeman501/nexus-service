using System;
using System.Diagnostics;
using System.Threading;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Peripherals.PixelFormats;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.JungleLeopard;

internal interface ID215JpegEncoder : IDisposable
{
    ReadOnlySpan<byte> Encode(ReadOnlySpan<byte> frame);
}

/// <summary>Assembles raw writes while one serial-pump consumer encodes the latest complete frame.</summary>
internal sealed class D215BgraFrames : ID215FrameSource, IDisposable
{
    internal const int Width = 756;
    internal const int Height = 1680;
    private const int BytesPerPixel = 4;
    private const long ReportIntervalMs = 30_000;
    private static readonly int[] Qualities = [BgraJpegEncoder.Quality, 70, 55];

    private readonly object _queueLock = new();
    private readonly int _frameBytes;
    private readonly Func<int, ID215JpegEncoder> _makeEncoder;
    private readonly Func<bool>? _mayRepeat;
    private readonly ID215JpegEncoder?[] _encoders = new ID215JpegEncoder?[Qualities.Length];
    private readonly byte[]? _portrait;
    private byte[] _partial;
    private byte[] _pending;
    private byte[] _active;
    private int _partialLength;
    private long _pendingAt;
    private bool _hasPending;
    private bool _disposed;
    private long _droppedFrames;
    private long _reportedDrops;
    private long? _lastDropReport;
    private long _freshFrames;
    private long _encodeTicks;
    private long _freshAgeTicks;
    private long? _lastTimingReport;

    internal D215BgraFrames(int frameBytes, Func<int, ID215JpegEncoder> makeEncoder, Func<bool>? mayRepeat = null)
        : this(frameBytes, makeEncoder, mayRepeat, rotateLandscape: false)
    {
    }

    private D215BgraFrames(int frameBytes, Func<int, ID215JpegEncoder> makeEncoder, Func<bool>? mayRepeat,
        bool rotateLandscape)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameBytes);
        _frameBytes = frameBytes;
        _makeEncoder = makeEncoder;
        _mayRepeat = mayRepeat;
        _partial = new byte[frameBytes];
        _pending = new byte[frameBytes];
        _active = new byte[frameBytes];
        if (rotateLandscape) _portrait = new byte[frameBytes];
    }

    internal static D215BgraFrames Create(Func<bool> mayRepeat, bool rotateLandscape = false) =>
        new(Width * Height * BytesPerPixel, static quality => new JpegEncoder(quality), mayRepeat, rotateLandscape);

    internal long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    public bool MayRepeat => _mayRepeat?.Invoke() == true;

    internal void Queue(ReadOnlySpan<byte> bytes)
    {
        lock (_queueLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_partialLength != 0)
            {
                var take = Math.Min(bytes.Length, _frameBytes - _partialLength);
                bytes[..take].CopyTo(_partial.AsSpan(_partialLength));
                _partialLength += take;
                if (_partialLength != _frameBytes) return;
                CompleteFrame();
                bytes = bytes[take..];
            }

            // Only the newest complete frame from a coalesced write can leave this queue.
            var completeFrames = bytes.Length / _frameBytes;
            if (completeFrames > 0)
            {
                var skipped = completeFrames - 1;
                Interlocked.Add(ref _droppedFrames, skipped);
                bytes = bytes[(skipped * _frameBytes)..];
                bytes[.._frameBytes].CopyTo(_partial);
                CompleteFrame();
                bytes = bytes[_frameBytes..];
            }

            bytes.CopyTo(_partial);
            _partialLength = bytes.Length;
        }
    }

    private void CompleteFrame()
    {
        if (_hasPending) Interlocked.Increment(ref _droppedFrames);
        (_partial, _pending) = (_pending, _partial);
        _hasPending = true;
        _pendingAt = Stopwatch.GetTimestamp();
        _partialLength = 0;
    }

    public ReadOnlyMemory<byte> TakeLatestEnvelope()
    {
        long queuedAt;
        lock (_queueLock)
        {
            if (!_hasPending || _disposed) return ReadOnlyMemory<byte>.Empty;
            (_pending, _active) = (_active, _pending);
            _hasPending = false;
            queuedAt = _pendingAt;
        }

        var encodeStartedAt = Stopwatch.GetTimestamp();
        var envelope = EncodeActiveFrame();
        if (envelope.IsEmpty) Interlocked.Increment(ref _droppedFrames);
        else ReportFreshFrameTiming(queuedAt, encodeStartedAt);
        ReportDrops();
        return envelope;
    }

    private ReadOnlyMemory<byte> EncodeActiveFrame()
    {
        ReadOnlySpan<byte> pixels = _active;
        if (_portrait is not null)
        {
            // Firmware 3.4 rotates the portrait JPEG clockwise on a wide-mounted panel.
            BgraQuarterTurn.RotateCcw(_active, Height, Width, _portrait);
            pixels = _portrait;
        }
        for (var i = 0; i < Qualities.Length; i++)
        {
            try
            {
                var encoder = _encoders[i] ??= _makeEncoder(Qualities[i]);
                var jpeg = encoder.Encode(pixels);
                if (!jpeg.IsEmpty && jpeg.Length <= D215Protocol.MaximumJpegBytes)
                    return D215Protocol.BuildJpegEnvelope(jpeg);
            }
            catch (InvalidOperationException)
            {
                // An encode failure drops this frame if all configured attempts fail.
            }
        }
        return ReadOnlyMemory<byte>.Empty;
    }

    private void ReportFreshFrameTiming(long queuedAt, long encodeStartedAt)
    {
        // The pump repeats retained envelopes without returning through the encoder.
        var encodedAt = Stopwatch.GetTimestamp();
        _freshFrames++;
        _encodeTicks += Stopwatch.GetElapsedTime(encodeStartedAt, encodedAt).Ticks;
        _freshAgeTicks += Stopwatch.GetElapsedTime(queuedAt, encodedAt).Ticks;

        var now = Environment.TickCount64;
        if (_lastTimingReport is long last && now - last < ReportIntervalMs) return;
        var ticksPerFrameMillisecond = (double)_freshFrames * TimeSpan.TicksPerMillisecond;
        var ageMs = _freshAgeTicks / ticksPerFrameMillisecond;
        var encodeMs = _encodeTicks / ticksPerFrameMillisecond;
        ServiceLog.Info($"[jungle-leopard-d215] fresh frames={_freshFrames}, "
            + $"avg queue={ageMs - encodeMs:F2} ms, encode={encodeMs:F2} ms, age={ageMs:F2} ms");
        _freshFrames = _encodeTicks = _freshAgeTicks = 0;
        _lastTimingReport = now;
    }

    private void ReportDrops()
    {
        var now = Environment.TickCount64;
        var total = DroppedFrames;
        if (total == _reportedDrops || (_lastDropReport is long last && now - last < ReportIntervalMs)) return;
        ServiceLog.Warn($"[jungle-leopard-d215] dropped {total - _reportedDrops} replaced or unencodable frames ({total} total)");
        _reportedDrops = total;
        _lastDropReport = now;
    }

    public void Dispose()
    {
        // The transport retires this source only after the sole encoding consumer exits.
        lock (_queueLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        foreach (var encoder in _encoders) encoder?.Dispose();
    }

    private sealed class JpegEncoder(int quality) : ID215JpegEncoder
    {
        private readonly BgraJpegEncoder _encoder = new(Width, Height, quality);
        public ReadOnlySpan<byte> Encode(ReadOnlySpan<byte> frame) => _encoder.Encode(frame);
        public void Dispose() => _encoder.Dispose();
    }
}
