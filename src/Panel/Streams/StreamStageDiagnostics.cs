using Nexus.Service.Diagnostics;
using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nexus.Service.Panel.Streams;

internal readonly record struct StreamStageSnapshot(long Accepted, long AcceptedBytes, long Enqueued,
    long Dequeued, long Trimmed, long ResyncDropped, long Cleared, long RejectedRaw,
    long TransportCalls, long TransportFailures, long TransportBytes, int MaximumDepth,
    DiagnosticTimingSnapshot IngestInterval, DiagnosticTimingSnapshot TransportCall);

internal sealed class StreamStageDiagnostics
{
    private readonly object _gate = new();
    private readonly string _tag;
    private readonly long _frequency;
    private readonly DiagnosticReportGate _report;
    private readonly DiagnosticTimingHistogram _ingest = new(), _transport = new();
    private long _accepted, _acceptedBytes, _enqueued, _dequeued, _trimmed, _resyncDropped, _cleared, _rejectedRaw;
    private long _transportCalls, _transportFailures, _transportBytes, _lastIngest;
    private int _maximumDepth;

    internal StreamStageDiagnostics(string sessionId, long frequency, long createdAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        _frequency = frequency;
        _report = new(frequency, createdAt);
        _tag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)))[..12];
    }

    internal void Accepted(long now, int bytes)
    {
        lock (_gate)
        {
            _accepted++; _acceptedBytes += bytes;
            if (_lastIngest != 0) _ingest.AddElapsed(_lastIngest, now, _frequency);
            _lastIngest = now;
        }
    }
    internal void ResetIngestClock() { lock (_gate) _lastIngest = 0; }
    internal void Enqueued(int depth) { lock (_gate) { _enqueued++; _maximumDepth = Math.Max(_maximumDepth, depth); } }
    internal void Dequeued(int count) { lock (_gate) _dequeued += count; }
    internal void Dropped(long trimmed, long resync, long cleared)
    {
        lock (_gate) { _trimmed += trimmed; _resyncDropped += resync; _cleared += cleared; }
    }
    internal void RejectedRaw() { lock (_gate) _rejectedRaw++; }
    internal void TransportCompleted(bool success, int bytes, long started, long completed)
    {
        lock (_gate)
        {
            _transport.AddElapsed(started, completed, _frequency);
            if (success) { _transportCalls++; _transportBytes += bytes; }
            else _transportFailures++;
        }
    }
    internal StreamStageSnapshot Snapshot() { lock (_gate) return SnapshotLocked(); }
    private StreamStageSnapshot SnapshotLocked() => new(_accepted, _acceptedBytes, _enqueued,
        _dequeued, _trimmed, _resyncDropped, _cleared, _rejectedRaw, _transportCalls,
        _transportFailures, _transportBytes, _maximumDepth, _ingest.Snapshot(), _transport.Snapshot());

    internal string? Report(long now, int depth, int maximumQueuedFrames, StreamCodec codec,
        int width, int height, int fps, bool final = false)
    {
        StreamStageSnapshot s;
        lock (_gate) { if (!_report.Take(now, final)) return null; s = SnapshotLocked(); }
        return string.Create(CultureInfo.InvariantCulture,
            $"[efficiency-stream] v=1 session={_tag} codec={(codec == StreamCodec.RawBgra ? "rawBgra" : "h264")} size={width}x{height} fps={fps} final={final} cumulative accepted={s.Accepted} acceptedBytes={s.AcceptedBytes} enqueued={s.Enqueued} dequeued={s.Dequeued} trimmed={s.Trimmed} resyncDropped={s.ResyncDropped} cleared={s.Cleared} rejectedRaw={s.RejectedRaw} depth={depth} maxDepth={s.MaximumDepth} queueCap={maximumQueuedFrames} transportCallsOk={s.TransportCalls} transportCallsFailed={s.TransportFailures} transportBytesOk={s.TransportBytes} ingestInterval[{s.IngestInterval}] transportCall[{s.TransportCall}] transportCallIsNotSerialCompletion=true");
    }
}
