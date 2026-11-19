using System;
using System.Diagnostics;
using System.Threading;
using Nexus.Service.Diagnostics;
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
    private readonly D215EnvelopePool _envelopes = new();
    private readonly int _frameBytes;
    private readonly Func<int, ID215JpegEncoder> _makeEncoder;
    private readonly Func<bool>? _mayRepeat;
    private readonly D215FrameDiagnostics? _explicitDiagnostics;
    private readonly DiagnosticLeaseOwner<D215FrameDiagnostics> _diagnosticOwner = new();
    private D215FrameDiagnostics? _diagnostics => _explicitDiagnostics ?? _diagnosticOwner.Get(static () => new D215FrameDiagnostics());
    private readonly ID215JpegEncoder?[] _encoders = new ID215JpegEncoder?[Qualities.Length];
    private readonly byte[]? _portrait;
    private byte[]? _oriented;
    private Func<bool>? _readFlip180;
    private byte[] _partial;
    private byte[] _pending;
    private byte[] _active;
    private int _partialLength;
    private long _pendingAt;
    private bool _hasPending;
    private bool _hasActive;
    private bool _activeFlip180;
    private bool _disposed;
    private long _droppedFrames;
    private long _reportedDrops;
    private long? _lastDropReport;
    private long _freshFrames;
    private long _encodeTicks;
    private long _freshAgeTicks;
    private long? _lastTimingReport;

    internal D215BgraFrames(int frameBytes, Func<int, ID215JpegEncoder> makeEncoder, Func<bool>? mayRepeat = null,
        D215FrameDiagnostics? diagnostics = null)
        : this(frameBytes, makeEncoder, mayRepeat, rotateLandscape: false, diagnostics)
    {
    }

    private D215BgraFrames(int frameBytes, Func<int, ID215JpegEncoder> makeEncoder, Func<bool>? mayRepeat,
        bool rotateLandscape, D215FrameDiagnostics? diagnostics = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameBytes);
        _frameBytes = frameBytes;
        _makeEncoder = makeEncoder;
        _mayRepeat = mayRepeat;
        _explicitDiagnostics = diagnostics;
        _partial = new byte[frameBytes];
        _pending = new byte[frameBytes];
        _active = new byte[frameBytes];
        if (rotateLandscape) _portrait = new byte[frameBytes];
    }

    internal static D215BgraFrames Create(Func<bool> mayRepeat, bool rotateLandscape = false) =>
        new(Width * Height * BytesPerPixel, static quality => new JpegEncoder(quality), mayRepeat, rotateLandscape);

    internal long DroppedFrames => Interlocked.Read(ref _droppedFrames);
    internal D215FrameDiagnostics? Diagnostics => _diagnostics;
    D215FrameDiagnostics? ID215FrameSource.Diagnostics => _diagnostics;

    internal void BindFlip180(Func<bool> source) => _readFlip180 = source;

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
                _diagnostics?.Coalesced(skipped);
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
        if (_hasPending)
        {
            Interlocked.Increment(ref _droppedFrames);
            _diagnostics?.Replaced();
        }
        (_partial, _pending) = (_pending, _partial);
        _hasPending = true;
        _pendingAt = Stopwatch.GetTimestamp();
        _partialLength = 0;
    }

    public D215EnvelopeLease TakeLatestEnvelope()
    {
        var diagnostics = _diagnostics;
        if (!_envelopes.TryRent(out var reservation, out var retired))
        {
            if (retired) diagnostics?.RetiredTake();
            else diagnostics?.PoolExhausted();
            ReportDiagnostics();
            return default;
        }
        var transferred = false;
        try
        {
            var flip180 = _readFlip180?.Invoke() == true;
            // The gate belongs to the coordinator; never call it while holding the
            // producer queue lock, since it may acquire the coordinator's lock.
            var mayRepeat = MayRepeat;
            long queuedAt;
            bool fresh;
            lock (_queueLock)
            {
                if (_disposed) return default;
                fresh = _hasPending;
                if (fresh)
                {
                    (_pending, _active) = (_active, _pending);
                    _hasPending = false;
                    _hasActive = true;
                }
                else if (!_hasActive || flip180 == _activeFlip180 || !mayRepeat)
                {
                    return default;
                }
                queuedAt = _pendingAt;
            }
            diagnostics?.Taken(fresh);

            var encodeStartedAt = Stopwatch.GetTimestamp();
            var envelope = EncodeActiveFrame(flip180, reservation, diagnostics);
            if (diagnostics is not null)
                diagnostics.Encoded(fresh, !envelope.IsEmpty, queuedAt, encodeStartedAt, Stopwatch.GetTimestamp());
            if (envelope.IsEmpty)
            {
                Interlocked.Increment(ref _droppedFrames);
                diagnostics?.Rejected();
            }
            else
            {
                _activeFlip180 = flip180;
                if (fresh) ReportFreshFrameTiming(queuedAt, encodeStartedAt);
            }
            ReportDrops();
            ReportDiagnostics();
            transferred = !envelope.IsEmpty;
            return envelope;
        }
        finally { if (!transferred) reservation.Dispose(); }
    }

    private D215EnvelopeLease EncodeActiveFrame(bool flip180, D215EnvelopeLease reservation, D215FrameDiagnostics? diagnostics)
    {
        ReadOnlySpan<byte> pixels = _active;
        if (flip180)
        {
            // Keep the retained source canonical so a still image can be turned back
            // without waiting for the renderer to produce another complete frame.
            _oriented ??= new byte[_frameBytes];
            BgraOrientation.Apply(_active, _portrait is null ? Width : Height,
                _portrait is null ? Height : Width, flip180: true, mirror: false, _oriented);
            pixels = _oriented;
        }
        if (_portrait is not null)
        {
            // Firmware 3.4 rotates the portrait JPEG clockwise on a wide-mounted panel.
            BgraQuarterTurn.RotateCcw(pixels, Height, Width, _portrait);
            pixels = _portrait;
        }
        for (var i = 0; i < Qualities.Length; i++)
        {
            var started = diagnostics is not null ? Stopwatch.GetTimestamp() : 0;
            var outcome = D215EncodeOutcome.UnexpectedException;
            var outputBytes = 0;
            try
            {
                var encoder = _encoders[i] ??= _makeEncoder(Qualities[i]);
                var jpeg = encoder.Encode(pixels);
                outputBytes = jpeg.Length;
                outcome = jpeg.IsEmpty ? D215EncodeOutcome.Empty : jpeg.Length > D215Protocol.MaximumJpegBytes
                    ? D215EncodeOutcome.Oversize : D215EncodeOutcome.Accepted;
                if (!jpeg.IsEmpty && jpeg.Length <= D215Protocol.MaximumJpegBytes)
                    return reservation.Format(jpeg);
            }
            catch (InvalidOperationException)
            {
                outcome = D215EncodeOutcome.RejectedException;
                // An encode failure drops this frame if all configured attempts fail.
            }
            finally
            {
                if (diagnostics is not null)
                    diagnostics.Attempt(i, outcome, outputBytes, started, Stopwatch.GetTimestamp());
            }
        }
        return default;
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

    internal void ReportDiagnostics(bool final = false)
    {
        var epoch = EfficiencyDiagnosticOptions.SessionRevision;
        if (_diagnostics?.Report(Stopwatch.GetTimestamp(), _envelopes.LeasedCapacityBytes, final) is { } line)
            if (_explicitDiagnostics is not null || (epoch != 0 && epoch == EfficiencyDiagnosticOptions.SessionRevision)) ServiceLog.Info(line);
    }

    public void Dispose()
    {
        // The transport retires this source only after the sole encoding consumer exits.
        lock (_queueLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _envelopes.Dispose();
        foreach (var encoder in _encoders) encoder?.Dispose();
        ReportDiagnostics(final: true);
    }

    private sealed class JpegEncoder(int quality) : ID215JpegEncoder
    {
        private readonly BgraJpegEncoder _encoder = new(Width, Height, quality);
        public ReadOnlySpan<byte> Encode(ReadOnlySpan<byte> frame) => _encoder.Encode(frame);
        public void Dispose() => _encoder.Dispose();
    }
}
