using Nexus.Service.Peripherals.JungleLeopard;
using Nexus.Service.Diagnostics;

namespace Nexus.Service.Tests.JungleLeopard;

public sealed class D215DiagnosticsTests
{
    [Fact]
    public void Partial_completion_and_coalesced_input_reconcile_distinct_drop_reasons()
    {
        var metrics = new D215FrameDiagnostics();
        using var frames = new D215BgraFrames(2, _ => new Encoder([9]), diagnostics: metrics);
        frames.Queue([1]);
        frames.Queue([2, 3, 4, 5, 6, 7]);
        using var lease = frames.TakeLatestEnvelope();
        Assert.False(lease.IsEmpty);
        var snapshot = metrics.Snapshot();
        Assert.Equal(1, snapshot.CoalescedInputs);
        Assert.Equal(1, snapshot.PendingReplacements);
        Assert.Equal(0, snapshot.AllQualityRejected);
        Assert.Equal(frames.DroppedFrames, snapshot.CombinedDrops);
        Assert.Equal(1, snapshot.FreshEncoded);
        Assert.Equal(1, snapshot.Qualities[0].Accepted);
    }

    [Fact]
    public void All_quality_rejection_is_one_drop_with_each_attempt_preserved()
    {
        var metrics = new D215FrameDiagnostics();
        using var frames = new D215BgraFrames(2, quality => quality switch
        {
            85 => new Encoder([]),
            70 => new Encoder(new byte[D215Protocol.MaximumJpegBytes + 1]),
            _ => new Encoder([], throws: true),
        }, diagnostics: metrics);
        frames.Queue([1, 2]);
        Assert.True(frames.TakeLatestEnvelope().IsEmpty);
        var snapshot = metrics.Snapshot();
        Assert.Equal(1, snapshot.AllQualityRejected);
        Assert.Equal(frames.DroppedFrames, snapshot.CombinedDrops);
        Assert.Equal(1, snapshot.Qualities[0].Empty);
        Assert.Equal(1, snapshot.Qualities[1].Oversize);
        Assert.Equal(D215Protocol.MaximumJpegBytes + 1, snapshot.Qualities[1].ReturnedBytes);
        Assert.Equal(D215Protocol.MaximumJpegBytes + 1, snapshot.Qualities[1].MaximumOutputBytes);
        Assert.Equal(1, snapshot.Qualities[2].RejectedExceptions);
    }

    [Fact]
    public void Pool_exhaustion_does_not_consume_pending_or_count_as_a_drop()
    {
        var metrics = new D215FrameDiagnostics();
        using var frames = new D215BgraFrames(2, _ => new Encoder([9]), diagnostics: metrics);
        frames.Queue([1, 2]);
        using var first = frames.TakeLatestEnvelope();
        frames.Queue([3, 4]);
        using var second = frames.TakeLatestEnvelope();
        frames.Queue([5, 6]);
        Assert.True(frames.TakeLatestEnvelope().IsEmpty);
        Assert.Equal(1, metrics.Snapshot().PoolExhausted);
        Assert.Equal(2, metrics.Snapshot().FreshTaken);
        first.Dispose();
        using var third = frames.TakeLatestEnvelope();
        Assert.False(third.IsEmpty);
        Assert.Equal(3, metrics.Snapshot().FreshTaken);
        Assert.Equal(0, metrics.Snapshot().CombinedDrops);
    }

    [Fact]
    public void A_successful_fallback_records_attempts_without_a_combined_drop()
    {
        var metrics = new D215FrameDiagnostics();
        using var frames = new D215BgraFrames(2, q => new Encoder(q == 85 ? [] : [7, 8]), diagnostics: metrics);
        frames.Queue([1, 2]);
        using var lease = frames.TakeLatestEnvelope();
        Assert.False(lease.IsEmpty);
        var snapshot = metrics.Snapshot();
        Assert.Equal(1, snapshot.Qualities[0].Empty);
        Assert.Equal(1, snapshot.Qualities[1].Accepted);
        Assert.Equal(2, snapshot.Qualities[1].OutputBytes);
        Assert.Equal(0, snapshot.Qualities[2].Attempts);
        Assert.Equal(0, snapshot.CombinedDrops);
    }

    [Fact]
    public void Queue_encode_age_quality_and_awaited_wire_completion_use_explicit_clock_units()
    {
        var metrics = new D215FrameDiagnostics(frequency: 1_000, createdAt: 0);
        metrics.Encoded(true, true, 1_000, 1_100, 1_150);
        metrics.Encoded(false, true, 0, 2_000, 2_020);
        metrics.Taken(false);
        metrics.Written(freshEnvelope: true, success: true, 8);
        metrics.Written(freshEnvelope: false, success: true, 12);
        metrics.Written(freshEnvelope: false, success: false, 100);
        metrics.Attempt(0, D215EncodeOutcome.UnexpectedException, 0, 10, 30);
        var snapshot = metrics.Snapshot();
        Assert.Equal(100, snapshot.Queue.MeanMilliseconds);
        Assert.Equal(35, snapshot.Encode.MeanMilliseconds);
        Assert.Equal(150, snapshot.Age.MeanMilliseconds);
        Assert.Equal(40, snapshot.Serial.MeanMilliseconds);
        Assert.Equal((1L, 1L, 1L), (snapshot.FreshWrites, snapshot.RepeatedWrites, snapshot.WriteFailures));
        Assert.Equal(1, snapshot.RotationEncoded);
        Assert.Equal(1, snapshot.RotationTaken);
        Assert.Equal(1, snapshot.Qualities[0].UnexpectedExceptions);
        Assert.Equal(20, snapshot.QualityTimes[0].MeanMilliseconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => metrics.Attempt(-1, D215EncodeOutcome.Accepted, 0, 0, 1));
    }

    [Fact]
    public void Unexpected_encoder_failure_is_reported_and_still_releases_its_reservation()
    {
        var metrics = new D215FrameDiagnostics();
        var fail = true;
        using var frames = new D215BgraFrames(2, _ => new UnexpectedEncoder(() => fail), diagnostics: metrics);
        frames.Queue([1, 2]);
        Assert.Throws<ApplicationException>(() => frames.TakeLatestEnvelope());
        Assert.Equal(1, metrics.Snapshot().Qualities[0].UnexpectedExceptions);
        Assert.Equal(0, metrics.Snapshot().AllQualityRejected);
        fail = false;
        frames.Queue([3, 4]);
        using var next = frames.TakeLatestEnvelope();
        Assert.False(next.IsEmpty);
        Assert.Equal(0, frames.DroppedFrames);
    }

    [Fact]
    public void Reports_are_rate_limited_and_final_is_once_with_an_explicit_lease_budget()
    {
        var metrics = new D215FrameDiagnostics(1_000, 0);
        Assert.Null(metrics.Report(29_999, 0));
        var line = metrics.Report(30_000, D215Protocol.MaximumJpegEnvelopeBytes);
        Assert.NotNull(line);
        Assert.Contains("leasedCapacityBytes=" + D215Protocol.MaximumJpegEnvelopeBytes, line);
        Assert.Contains("combined=0", line);
        Assert.True(line.Length < 3500);
        Assert.Null(metrics.Report(30_001, 0));
        Assert.NotNull(metrics.Report(30_002, 0, final: true));
        Assert.Null(metrics.Report(100_000, 0, final: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new D215FrameDiagnostics(0));
    }

    [Fact]
    public void Service_timing_buckets_share_the_explicit_fixed_boundaries_and_reject_invalid_samples()
    {
        var histogram = new DiagnosticTimingHistogram();
        Assert.Equal(0, histogram.Snapshot().MeanMilliseconds);
        foreach (var value in new double[] { 0, 1, 4, 8, 16, 33, 66, 100, 101 }) histogram.Add(value);
        histogram.Add(-1); histogram.Add(double.NaN); histogram.Add(double.PositiveInfinity);
        histogram.AddElapsed(10, 9, 1_000); histogram.AddElapsed(0, 1, 0);
        var snapshot = histogram.Snapshot();
        Assert.Equal(new long[] { 2, 1, 1, 1, 1, 1, 1, 1 }, snapshot.Buckets);
        Assert.Equal(9, snapshot.Count); Assert.Equal(5, snapshot.InvalidSamples);
        snapshot.Buckets[0] = -99;
        Assert.Equal(2, histogram.Snapshot().Buckets[0]);
    }

    [Fact]
    public void Envelope_lease_capacity_tracks_current_owners_through_idempotent_release_and_retirement()
    {
        using var pool = new D215EnvelopePool();
        Assert.Equal(0, pool.LeasedCapacityBytes);
        Assert.True(pool.TryRent(out var first));
        Assert.Equal(D215Protocol.MaximumJpegEnvelopeBytes, pool.LeasedCapacityBytes);
        Assert.True(pool.TryRent(out var second));
        Assert.Equal(D215EnvelopePool.CapacityBytes, pool.LeasedCapacityBytes);
        pool.Dispose();
        Assert.Equal(D215EnvelopePool.CapacityBytes, pool.LeasedCapacityBytes);
        first.Dispose(); first.Dispose(); second.Dispose();
        Assert.Equal(0, pool.LeasedCapacityBytes);
    }

    [Fact]
    public void Retirement_is_distinct_from_exhaustion_and_cannot_create_a_diagnostic_drop()
    {
        var metrics = new D215FrameDiagnostics();
        using var frames = new D215BgraFrames(2, _ => new Encoder([9]), diagnostics: metrics);
        frames.Dispose();
        Assert.True(frames.TakeLatestEnvelope().IsEmpty);
        Assert.Equal(0, metrics.Snapshot().PoolExhausted);
        Assert.Equal(1, metrics.Snapshot().RetiredTakes);
        Assert.Equal(0, metrics.Snapshot().CombinedDrops);
    }

    [Fact]
    public void Still_rotation_rejection_and_retry_keep_canonical_repeat_and_reason_counts()
    {
        var metrics = new D215FrameDiagnostics();
        bool flip = false, reject = false;
        using var frames = new D215BgraFrames(756 * 1680 * 4, _ => new RotationEncoder(() => reject), () => true, metrics);
        frames.BindFlip180(() => flip);
        var pixels = new byte[756 * 1680 * 4]; pixels[0] = 1; pixels[^4] = 2;
        frames.Queue(pixels);
        using (var initial = frames.TakeLatestEnvelope()) Assert.Equal(1, initial.Memory.Span[4]);
        flip = reject = true;
        Assert.True(frames.TakeLatestEnvelope().IsEmpty);
        reject = false;
        using (var rotated = frames.TakeLatestEnvelope()) Assert.Equal(2, rotated.Memory.Span[4]);
        Assert.True(frames.TakeLatestEnvelope().IsEmpty);
        var s = metrics.Snapshot();
        Assert.Equal((1L, 2L, 1L, 1L), (s.FreshTaken, s.RotationTaken, s.FreshEncoded, s.RotationEncoded));
        Assert.Equal(1, s.AllQualityRejected);
        Assert.Equal(frames.DroppedFrames, s.CombinedDrops);
        Assert.Equal(1, s.Queue.Count); // rotation age is not misreported as fresh queue delay
    }

    [Fact]
    public void Enabled_diagnostics_do_not_add_per_frame_allocations_after_warmup()
    {
        var metrics = new D215FrameDiagnostics();
        using var frames = new D215BgraFrames(2, _ => new Encoder([9]), diagnostics: metrics);
        for (var i = 0; i < 8; i++) { frames.Queue([1, 2]); using var lease = frames.TakeLatestEnvelope(); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) { frames.Queue([1, 2]); using var lease = frames.TakeLatestEnvelope(); }
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 4096);
    }

    private sealed class RotationEncoder(Func<bool> reject) : ID215JpegEncoder
    {
        public ReadOnlySpan<byte> Encode(ReadOnlySpan<byte> frame) => reject() ? [] : frame[..4];
        public void Dispose() { }
    }

    private sealed class UnexpectedEncoder(Func<bool> fail) : ID215JpegEncoder
    {
        public ReadOnlySpan<byte> Encode(ReadOnlySpan<byte> frame) => fail() ? throw new ApplicationException() : frame;
        public void Dispose() { }
    }

    private sealed class Encoder(byte[] bytes, bool throws = false) : ID215JpegEncoder
    {
        public ReadOnlySpan<byte> Encode(ReadOnlySpan<byte> frame) => throws ? throw new InvalidOperationException() : bytes;
        public void Dispose() { }
    }
}
