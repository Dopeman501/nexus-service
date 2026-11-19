using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Nexus.Service.Diagnostics;

namespace Nexus.Service.Peripherals.JungleLeopard;

internal enum D215EncodeOutcome { Accepted, Empty, Oversize, RejectedException, UnexpectedException }

internal readonly record struct D215QualityDiagnostics(long Attempts, long Accepted, long Empty,
    long Oversize, long RejectedExceptions, long UnexpectedExceptions, long OutputBytes,
    long ReturnedBytes, int MaximumOutputBytes);

internal readonly record struct D215DiagnosticSnapshot(long CoalescedInputs, long PendingReplacements,
    long AllQualityRejected, long FreshTaken, long RotationTaken, long PoolExhausted, long RetiredTakes,
    long FreshEncoded, long RotationEncoded, long FreshWrites, long RepeatedWrites, long WriteFailures,
    D215QualityDiagnostics[] Qualities, DiagnosticTimingSnapshot Queue, DiagnosticTimingSnapshot Encode,
    DiagnosticTimingSnapshot Age, DiagnosticTimingSnapshot Serial, DiagnosticTimingSnapshot[] QualityTimes)
{
    internal long CombinedDrops => CoalescedInputs + PendingReplacements + AllQualityRejected;
}

/// <summary>One optional D215 owner's fixed-capacity diagnostic accumulator.</summary>
internal sealed class D215FrameDiagnostics
{
    private static int _nextOwner;
    private readonly int _owner = Interlocked.Increment(ref _nextOwner);
    private readonly object _gate = new();
    private readonly long _frequency;
    private readonly DiagnosticReportGate _report;
    private readonly D215QualityDiagnostics[] _qualities = new D215QualityDiagnostics[3];
    private readonly DiagnosticTimingHistogram[] _qualityTimes = [new(), new(), new()];
    private readonly DiagnosticTimingHistogram _queue = new(), _encode = new(), _age = new(), _serial = new();
    private long _coalesced, _replaced, _rejected, _freshTaken, _rotationTaken, _poolExhausted, _retiredTakes;
    private long _freshEncoded, _rotationEncoded, _freshWrites, _repeatWrites, _writeFailures;

    internal D215FrameDiagnostics(long? frequency = null, long? createdAt = null)
    {
        _frequency = frequency ?? Stopwatch.Frequency;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_frequency);
        _report = new(_frequency, createdAt ?? Stopwatch.GetTimestamp());
    }

    internal void Coalesced(long count) { lock (_gate) _coalesced += count; }
    internal void Replaced() { lock (_gate) _replaced++; }
    internal void Rejected() { lock (_gate) _rejected++; }
    internal void PoolExhausted() { lock (_gate) _poolExhausted++; }
    internal void RetiredTake() { lock (_gate) _retiredTakes++; }
    internal void Taken(bool fresh) { lock (_gate) { if (fresh) _freshTaken++; else _rotationTaken++; } }

    internal void Attempt(int index, D215EncodeOutcome outcome, int outputBytes, long started, long completed)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, 3U);
        lock (_gate)
        {
            var old = _qualities[index];
            _qualities[index] = new(old.Attempts + 1, old.Accepted + (outcome == D215EncodeOutcome.Accepted ? 1 : 0),
                old.Empty + (outcome == D215EncodeOutcome.Empty ? 1 : 0),
                old.Oversize + (outcome == D215EncodeOutcome.Oversize ? 1 : 0),
                old.RejectedExceptions + (outcome == D215EncodeOutcome.RejectedException ? 1 : 0),
                old.UnexpectedExceptions + (outcome == D215EncodeOutcome.UnexpectedException ? 1 : 0),
                old.OutputBytes + (outcome == D215EncodeOutcome.Accepted ? outputBytes : 0),
                old.ReturnedBytes + outputBytes, Math.Max(old.MaximumOutputBytes, outputBytes));
            _qualityTimes[index].AddElapsed(started, completed, _frequency);
        }
    }

    internal void Encoded(bool fresh, bool success, long queued, long started, long completed)
    {
        lock (_gate)
        {
            _encode.AddElapsed(started, completed, _frequency);
            if (fresh)
            {
                _queue.AddElapsed(queued, started, _frequency);
                _age.AddElapsed(queued, completed, _frequency);
                if (success) _freshEncoded++;
            }
            else if (success) _rotationEncoded++;
        }
    }

    // A fresh wire write means a newly encoded envelope, including a still rotation.
    // A retained repeat bypasses encoding; only actual awaited terminal writes count.
    internal void Written(bool freshEnvelope, bool success, double elapsedMilliseconds)
    {
        lock (_gate)
        {
            _serial.Add(elapsedMilliseconds);
            if (!success) _writeFailures++;
            else if (freshEnvelope) _freshWrites++;
            else _repeatWrites++;
        }
    }

    internal D215DiagnosticSnapshot Snapshot() { lock (_gate) return SnapshotLocked(); }

    private D215DiagnosticSnapshot SnapshotLocked() => new(_coalesced, _replaced, _rejected,
        _freshTaken, _rotationTaken, _poolExhausted, _retiredTakes, _freshEncoded, _rotationEncoded,
        _freshWrites, _repeatWrites, _writeFailures, (D215QualityDiagnostics[])_qualities.Clone(),
        _queue.Snapshot(), _encode.Snapshot(), _age.Snapshot(), _serial.Snapshot(),
        [_qualityTimes[0].Snapshot(), _qualityTimes[1].Snapshot(), _qualityTimes[2].Snapshot()]);

    internal string? Report(long now, int leasedCapacityBytes, bool final = false)
    {
        D215DiagnosticSnapshot snapshot;
        lock (_gate)
        {
            if (!_report.Take(now, final)) return null;
            snapshot = SnapshotLocked();
        }
        return string.Create(CultureInfo.InvariantCulture,
            $"[efficiency-d215] v=1 owner={_owner} final={final} cumulative coalesced={snapshot.CoalescedInputs} replaced={snapshot.PendingReplacements} allQualityRejected={snapshot.AllQualityRejected} combined={snapshot.CombinedDrops} freshTaken={snapshot.FreshTaken} rotationTaken={snapshot.RotationTaken} freshEncoded={snapshot.FreshEncoded} rotationEncoded={snapshot.RotationEncoded} poolExhausted={snapshot.PoolExhausted} retiredTakes={snapshot.RetiredTakes} leasedCapacityBytes={leasedCapacityBytes} poolCapacityBytes={D215EnvelopePool.CapacityBytes} newEnvelopeWrites={snapshot.FreshWrites} retainedWrites={snapshot.RepeatedWrites} writeFailures={snapshot.WriteFailures} queue[{snapshot.Queue}] encode[{snapshot.Encode}] age[{snapshot.Age}] serialTerminal[{snapshot.Serial}] q85[{snapshot.Qualities[0]};{snapshot.QualityTimes[0]}] q70[{snapshot.Qualities[1]};{snapshot.QualityTimes[1]}] q55[{snapshot.Qualities[2]};{snapshot.QualityTimes[2]}]");
    }
}
