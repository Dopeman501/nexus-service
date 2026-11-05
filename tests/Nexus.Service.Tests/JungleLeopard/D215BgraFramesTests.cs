using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nexus.Service.Peripherals.JungleLeopard;

namespace Nexus.Service.Tests.JungleLeopard;

public sealed class D215BgraFramesTests
{
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
        Assert.Equal(1, frames.TakeLatestEnvelope().Span[4]);

        flip180 = reject = true;
        Assert.True(frames.TakeLatestEnvelope().IsEmpty);
        reject = false;

        Assert.Equal(2, frames.TakeLatestEnvelope().Span[4]);
        Assert.True(frames.TakeLatestEnvelope().IsEmpty);
        Assert.Equal(1, frames.DroppedFrames);
    }

    [Fact]
    public void Split_and_coalesced_writes_preserve_frame_boundaries_and_keep_the_latest()
    {
        using var frames = new D215BgraFrames(4, _ => new EchoEncoder());
        frames.Queue([1, 2]);
        Assert.True(frames.TakeLatestEnvelope().IsEmpty);

        frames.Queue([3, 4, 5, 6, 7, 8, 9, 10]);
        Assert.Equal(new byte[] { 4, 0, 0, 0, 5, 6, 7, 8, 30, 0 }, frames.TakeLatestEnvelope().ToArray());
        Assert.Equal(1, frames.DroppedFrames);
        Assert.True(frames.TakeLatestEnvelope().IsEmpty);

        frames.Queue([11, 12]);
        Assert.Equal(new byte[] { 4, 0, 0, 0, 9, 10, 11, 12, 46, 0 }, frames.TakeLatestEnvelope().ToArray());
    }

    [Fact]
    public void Coalesced_batches_replace_every_older_pending_frame()
    {
        using var frames = new D215BgraFrames(2, _ => new EchoEncoder());
        frames.Queue([1, 2]);
        frames.Queue([3, 4, 5, 6, 7, 8]);

        Assert.Equal(new byte[] { 2, 0, 0, 0, 7, 8, 17, 0 }, frames.TakeLatestEnvelope().ToArray());
        Assert.Equal(3, frames.DroppedFrames);
    }

    [Fact]
    public async Task A_slow_encoder_cannot_block_or_reuse_the_producers_buffer()
    {
        using var encoder = new BlockingEncoder();
        using var frames = new D215BgraFrames(2, _ => encoder);
        frames.Queue([1, 2]);
        var active = Task.Run(() => frames.TakeLatestEnvelope());
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
        Assert.Equal(new byte[] { 2, 0, 0, 0, 5, 6, 13, 0 }, frames.TakeLatestEnvelope().ToArray());
        Assert.Equal(1, frames.DroppedFrames);
    }

    [Fact]
    public void Encoded_envelope_is_independent_of_the_next_encoding()
    {
        using var frames = new D215BgraFrames(2, _ => new EchoEncoder());
        frames.Queue([1, 2]);
        var first = frames.TakeLatestEnvelope();

        frames.Queue([3, 4]);
        Assert.Equal(new byte[] { 2, 0, 0, 0, 3, 4, 9, 0 }, frames.TakeLatestEnvelope().ToArray());
        Assert.Equal(new byte[] { 2, 0, 0, 0, 1, 2, 5, 0 }, first.ToArray());
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
        Assert.Equal(new byte[] { 2, 0, 0, 0, 1, 2, 5, 0 }, frames.TakeLatestEnvelope().ToArray());
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
        Assert.True(frames.TakeLatestEnvelope().IsEmpty);
        Assert.Equal(1, frames.DroppedFrames);
        Assert.Equal(3, encoders.Count);

        encoders[0].Bytes = [1, 2];
        frames.Queue([3, 4]);
        Assert.False(frames.TakeLatestEnvelope().IsEmpty);
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
