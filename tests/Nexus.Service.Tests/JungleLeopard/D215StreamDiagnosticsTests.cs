using System.Diagnostics;
using System.Threading.Channels;
using Nexus.Service.Peripherals.JungleLeopard;

namespace Nexus.Service.Tests.JungleLeopard;

public sealed class D215StreamDiagnosticsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    [Fact]
    public async Task Fresh_and_retained_write_counts_advance_only_after_their_actual_awaited_completion()
    {
        var metrics = new D215FrameDiagnostics();
        using var frames = new D215BgraFrames(2, _ => new Encoder(), () => true, metrics);
        frames.Queue([1, 2]);
        await using var transport = new Transport();
        var clock = new ForwardingClock(); // no native frame timer or hardware
        var timing = D215Timing.Default with { ClearSettle = TimeSpan.Zero, FrameInterval = TimeSpan.FromMilliseconds(1) };
        var stream = new D215LiveStream(new D215Wire(transport, clock, timing), frames, () => true, clock, timing);
        try
        {
            var first = await transport.Frames.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
            Assert.Equal(0, metrics.Snapshot().FreshWrites);
            first.Release.TrySetResult();
            var repeated = await transport.Frames.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
            Assert.Equal(1, metrics.Snapshot().FreshWrites);
            Assert.Equal(0, metrics.Snapshot().RepeatedWrites);
            stream.RequestStop();
            repeated.Release.TrySetResult();
            await stream.Completion.WaitAsync(Timeout);
            var snapshot = metrics.Snapshot();
            Assert.Equal((1L, 1L, 0L), (snapshot.FreshWrites, snapshot.RepeatedWrites, snapshot.WriteFailures));
            Assert.Equal(1, snapshot.FreshEncoded);
            Assert.Equal(2, snapshot.Serial.Count);
            Assert.Null(stream.Failure);
        }
        finally { transport.ReleaseAll(); stream.Abort(); await stream.Completion.WaitAsync(Timeout); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_cancellation_ignoring_write_is_not_reported_before_native_adapter_settles(bool uncertain)
    {
        var metrics = new D215FrameDiagnostics();
        using var frames = new D215BgraFrames(2, _ => new Encoder(), () => true, metrics);
        frames.Queue([1, 2]);
        await using var transport = new Transport();
        var clock = new ForwardingClock();
        var timing = D215Timing.Default with { WriteTimeout = TimeSpan.FromMilliseconds(uncertain ? 30 : 2_000) };
        var stream = new D215LiveStream(new D215Wire(transport, clock, timing), frames, () => true, clock, timing);
        try
        {
            var held = await transport.Frames.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
            if (uncertain) await held.CancellationObserved.Task.WaitAsync(Timeout);
            Assert.Equal(0, metrics.Snapshot().Serial.Count);
            Assert.Equal(0, metrics.Snapshot().WriteFailures);
            Assert.False(stream.Completion.IsCompleted);
            if (uncertain) held.Release.TrySetResult();
            else held.Release.TrySetException(new IOException("Injected adapter failure"));
            await stream.Completion.WaitAsync(Timeout);
            Assert.Equal(1, metrics.Snapshot().WriteFailures);
            Assert.Equal(1, metrics.Snapshot().Serial.Count);
            Assert.Equal(0, metrics.Snapshot().FreshWrites);
            Assert.NotNull(stream.Failure);
        }
        finally { transport.ReleaseAll(); stream.Abort(); await stream.Completion.WaitAsync(Timeout); }
    }

    private sealed class Encoder : ID215JpegEncoder
    {
        public ReadOnlySpan<byte> Encode(ReadOnlySpan<byte> frame) => new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        public void Dispose() { }
    }

    private sealed class ForwardingClock : TimeProvider
    {
        public override long TimestampFrequency => Stopwatch.Frequency;
        public override long GetTimestamp() => Stopwatch.GetTimestamp();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            System.CreateTimer(callback, state, dueTime, period);
    }

    private sealed class HeldWrite
    {
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Transport : ID215SerialTransport
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        internal Channel<HeldWrite> Frames { get; } = Channel.CreateUnbounded<HeldWrite>();
        private readonly List<HeldWrite> _held = [];
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
        {
            if (source.Span.SequenceEqual(Convert.FromHexString("55AA0700111701")))
            {
                _replies.Writer.TryWrite(Convert.FromHexString("55AA080011001801"));
                return;
            }
            if (source.Length != 10) return; // final clear/control commands
            var held = new HeldWrite();
            lock (_held) _held.Add(held);
            using var registration = cancellationToken.Register(() => held.CancellationObserved.TrySetResult());
            Frames.Writer.TryWrite(held);
            await held.Release.Task; // deliberately retains the buffer until actual completion
        }
        public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            var reply = await _replies.Reader.ReadAsync(cancellationToken);
            reply.CopyTo(destination);
            return reply.Length;
        }
        internal void ReleaseAll() { lock (_held) foreach (var item in _held) item.Release.TrySetResult(); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
