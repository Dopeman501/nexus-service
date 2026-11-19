using System;
using System.Globalization;

namespace Nexus.Service.Diagnostics;

internal readonly record struct DiagnosticTimingSnapshot(long Count, long InvalidSamples,
    double SumMilliseconds, double MaximumMilliseconds, long[] Buckets)
{
    internal double MeanMilliseconds => Count == 0 ? 0 : SumMilliseconds / Count;
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"n={Count},invalid={InvalidSamples},meanMs={MeanMilliseconds:F3},maxMs={MaximumMilliseconds:F3},buckets={string.Join('/', Buckets)}");
}

/// <summary>Eight fixed buckets: ≤1/4/8/16/33/66/100ms and above100ms. Owner synchronizes access.</summary>
internal sealed class DiagnosticTimingHistogram
{
    private readonly long[] _buckets = new long[8];
    private long _count;
    private long _invalid;
    private double _sum;
    private double _maximum;

    internal void Add(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0) { _invalid++; return; }
        var bucket = milliseconds <= 1 ? 0 : milliseconds <= 4 ? 1 : milliseconds <= 8 ? 2
            : milliseconds <= 16 ? 3 : milliseconds <= 33 ? 4 : milliseconds <= 66 ? 5 : milliseconds <= 100 ? 6 : 7;
        _buckets[bucket]++;
        _count++;
        _sum += milliseconds;
        _maximum = Math.Max(_maximum, milliseconds);
    }

    internal void AddElapsed(long start, long end, long frequency) =>
        Add(end < start || frequency <= 0 ? double.NaN : (end - (double)start) * 1000 / frequency);

    internal DiagnosticTimingSnapshot Snapshot() => new(_count, _invalid, _sum, _maximum, (long[])_buckets.Clone());
}

/// <summary>No timer; the existing owner loop checks one monotonic30second gate.</summary>
internal sealed class DiagnosticReportGate(long frequency, long createdAt)
{
    private long _lastReport = createdAt;
    private bool _final;

    internal bool Take(long now, bool final)
    {
        if (_final) return false;
        if (!final && (now < _lastReport || (now - (double)_lastReport) / frequency < 30)) return false;
        _lastReport = now;
        _final = final;
        return true;
    }
}
