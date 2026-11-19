using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nexus.Service.Peripherals.JungleLeopard;
using Xunit.Abstractions;

namespace Nexus.Service.Tests.JungleLeopard;

public sealed class D215BgraFramesTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(32 * 1024)]
    [InlineData(100 * 1024)]
    [InlineData(305 * 1024)]
    public void Warm_fresh_frame_assembly_does_not_allocate_payload_sized_envelopes(int payloadBytes)
    {
        using var frames = new D215BgraFrames(2, _ => new FixedEncoder(new byte[payloadBytes]));
        for (var i = 0; i < 8; i++) { frames.Queue([1, 2]); using var lease = frames.TakeLatestEnvelope(); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) { frames.Queue([1, 2]); using var lease = frames.TakeLatestEnvelope(); }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"fresh-envelope payload={payloadBytes}; samples=100; allocated={allocated} bytes; pool=624652 bytes");
        Assert.True(allocated < 4096, $"Fresh-envelope assembly allocated {allocated} bytes for {payloadBytes}-byte payloads.");
    }

    [Fact]
    public void A_failed_still_rotation_retries_the_canonical_frame_without_new_input()
    {
        var reject = false;
        var flip180 = false;
        using var frames = new D215BgraFrames(756 * 1680 * 4, _ => new ConditionalEncoder(() => reject), () => true);
        frames.BindFlip180(() => flip180);
        var frame = new byte[756 * 1680 * 4];
        frame[0] = 1;
        frame[^4] = 2;
        frames.Queue(frame);
        Assert.Equal(1, frames.TakeEnvelopeSnapshot().Span[4]);

        flip180 = reject = true;
        Assert.True(frames.TakeEnvelopeSnapshot().IsEmpty);
        reject = false;

        Assert.Equal(2, frames.TakeEnvelopeSnapshot().Span[4]);
        Assert.True(frames.TakeEnvelopeSnapshot().IsEmpty);
        Assert.Equal(1, frames.DroppedFrames);
    }

    [Fact]
    public void Split_and_coalesced_writes_preserve_frame_boundaries_and_keep_the_latest()
    {
        using var frames = new D215BgraFrames(4, _ => new EchoEncoder());
        frames.Queue([1, 2]);
        Assert.True(frames.TakeEnvelopeSnapshot().IsEmpty);

        frames.Queue([3, 4, 5, 6, 7, 8, 9, 10]);
        Assert.Equal(new byte[] { 4, 0, 0, 0, 5, 6, 7, 8, 30, 0 }, frames.TakeEnvelopeSnapshot().ToArray());
        Assert.Equal(1, frames.DroppedFrames);
        Assert.True(frames.TakeEnvelopeSnapshot().IsEmpty);

        frames.Queue([11, 12]);
        Assert.Equal(new byte[] { 4, 0, 0, 0, 9, 10, 11, 12, 46, 0 }, frames.TakeEnvelopeSnapshot().ToArray());
    }

    [Fact]
    public void Coalesced_batches_replace_every_older_pending_frame()
    {
        using var frames = new D215BgraFrames(2, _ => new EchoEncoder());
        frames.Queue([1, 2]);
        frames.Queue([3, 4, 5, 6, 7, 8]);

        Assert.Equal(new byte[] { 2, 0, 0, 0, 7, 8, 17, 0 }, frames.TakeEnvelopeSnapshot().ToArray());
        Assert.Equal(3, frames.DroppedFrames);
    }

    [Fact]
    public async Task A_slow_encoder_cannot_block_or_reuse_the_producers_buffer()
    {
        using var encoder = new BlockingEncoder();
        using var frames = new D215BgraFrames(2, _ => encoder);
        frames.Queue([1, 2]);
        var active = Task.Run(() => frames.TakeEnvelopeSnapshot());
        try
        {
            await encoder.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Run(() => frames.Queue([3, 4, 5, 6])).WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            encoder.Finish.TrySetResult();
        }

        var first = await active.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new byte[] { 2, 0, 0, 0, 1, 2, 5, 0 }, first.ToArray());
        Assert.Equal(new byte[] { 2, 0, 0, 0, 5, 6, 13, 0 }, frames.TakeEnvelopeSnapshot().ToArray());
        Assert.Equal(1, frames.DroppedFrames);
    }

    [Fact]
    public void Encoded_envelope_is_independent_of_the_next_encoding()
    {
        using var frames = new D215BgraFrames(2, _ => new EchoEncoder());
        frames.Queue([1, 2]);
        using var first = frames.TakeLatestEnvelope();

        frames.Queue([3, 4]);
        using var second = frames.TakeLatestEnvelope();
        Assert.Equal(new byte[] { 2, 0, 0, 0, 3, 4, 9, 0 }, second.Memory.ToArray());
        Assert.Equal(new byte[] { 2, 0, 0, 0, 1, 2, 5, 0 }, first.Memory.ToArray());
    }

    [Fact]
    public void Exhausted_envelope_slots_keep_the_pending_frame_until_a_lease_really_returns()
    {
        using var frames = new D215BgraFrames(2, _ => new EchoEncoder());
        frames.Queue([1, 2]);
        using var first = frames.TakeLatestEnvelope();
        frames.Queue([3, 4]);
        using var second = frames.TakeLatestEnvelope();
        frames.Queue([5, 6]);
        Assert.True(frames.TakeLatestEnvelope().IsEmpty);
        Assert.Equal(new byte[] { 2, 0, 0, 0, 1, 2, 5, 0 }, first.Memory.ToArray());
        first.Dispose();
        using var third = frames.TakeLatestEnvelope();
        Assert.Equal(new byte[] { 2, 0, 0, 0, 5, 6, 13, 0 }, third.Memory.ToArray());
        Assert.Equal(new byte[] { 2, 0, 0, 0, 3, 4, 9, 0 }, second.Memory.ToArray());
        first.Dispose();
        frames.Queue([7, 8]);
        Assert.True(frames.TakeLatestEnvelope().IsEmpty);
        Assert.Equal(0, frames.DroppedFrames);
    }

    [Fact]
    public void Oversized_frame_uses_bounded_fallback_without_eagerly_creating_other_encoders()
    {
        var qualities = new List<int>();
        using var frames = new D215BgraFrames(2, quality =>
        {
            qualities.Add(quality);
            return quality == 85 ? new FixedEncoder(new byte[305 * 1024 + 1]) : new EchoEncoder();
        });

        frames.Queue([1, 2]);
        Assert.Equal(new byte[] { 2, 0, 0, 0, 1, 2, 5, 0 }, frames.TakeEnvelopeSnapshot().ToArray());
        Assert.Equal(new[] { 85, 70 }, qualities);
        Assert.Equal(0, frames.DroppedFrames);
    }

    [Fact]
    public void Rejected_frame_does_not_prevent_later_frames_and_encoders_are_disposed_once()
    {
        var encoders = new List<FixedEncoder>();
        using var frames = new D215BgraFrames(2, quality =>
        {
            var encoder = new FixedEncoder(quality == 85 ? [] : new byte[305 * 1024 + 1]);
            encoders.Add(encoder);
            return encoder;
        });

        frames.Queue([1, 2]);
        Assert.True(frames.TakeEnvelopeSnapshot().IsEmpty);
        Assert.Equal(1, frames.DroppedFrames);
        Assert.Equal(3, encoders.Count);

        encoders[0].Bytes = [1, 2];
        frames.Queue([3, 4]);
        Assert.False(frames.TakeEnvelopeSnapshot().IsEmpty);
        frames.Dispose();
        frames.Dispose();
        Assert.All(encoders, encoder => Assert.Equal(1, encoder.Disposals));
        Assert.Throws<ObjectDisposedException>(() => frames.Queue([1, 2]));
    }

    private sealed class EchoEncoder : ID215JpegEncoder
    {
        private readonly byte[] _reused = new byte[4];
        public ReadOnlySpan<byte> Encode(ReadOnlySpan<byte> frame)
        {
            frame.CopyTo(_reused);
            return _reused.AsSpan(0, frame.Length);
        }
        public void Dispose() { }
    }

    private sealed class ConditionalEncoder(Func<bool> reject) : ID215JpegEncoder
    {
        public ReadOnlySpan<byte> Encode(ReadOnlySpan<byte> frame) => reject() ? [] : frame[..4];
        public void Dispose() { }
    }

    private sealed class FixedEncoder(byte[] bytes) : ID215JpegEncoder
    {
        public byte[] Bytes { get; set; } = bytes;
        public int Disposals { get; private set; }
        public ReadOnlySpan<byte> Encode(ReadOnlySpan<byte> frame) => Bytes;
        public void Dispose() => Disposals++;
    }

    private sealed class BlockingEncoder : ID215JpegEncoder
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ReadOnlySpan<byte> Encode(ReadOnlySpan<byte> frame)
        {
            Started.TrySetResult();
            Finish.Task.Wait(TimeSpan.FromSeconds(5));
            return frame;
        }
        public void Dispose() { }
    }
}
