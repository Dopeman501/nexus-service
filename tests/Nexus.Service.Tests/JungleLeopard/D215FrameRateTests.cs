using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading.Channels;
using Nexus.Service.Peripherals.JungleLeopard;

namespace Nexus.Service.Tests.JungleLeopard;

public sealed class D215FrameRateTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly D215Port Port = new("COM7", "fps-test", true);
    private static readonly D215Timing Timing = D215Timing.Default with { PortSettle = TimeSpan.Zero, ClearSettle = TimeSpan.Zero };

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(60)]
    public async Task Selected_cap_paces_wire_frames_including_encode_and_write_work(int fps)
    {
        var clock = new ManualClock();
        var wire = new ClockWire(clock, TimeSpan.FromMilliseconds(3));
        await using var session = await Connect(wire, clock);
        var frames = new FreshFrames(clock, TimeSpan.FromMilliseconds(2));
        var stream = session.BeginStreaming(frames, () => true, fps);
        try
        {
            var first = await wire.NextJpeg();
            var interval = TimeSpan.FromSeconds(1.0 / fps);
            await WaitUntil(() => wire.JpegCount == 1 && clock.Elapsed == TimeSpan.FromMilliseconds(5));
            var nextDue = clock.Elapsed + TimeSpan.FromMilliseconds(Math.Ceiling((interval - clock.Elapsed).TotalMilliseconds));
            await WaitUntil(() => clock.HasTimerAt(nextDue.Ticks));
            clock.Advance(nextDue - clock.Elapsed - TimeSpan.FromTicks(1));
            Assert.Equal(1, wire.JpegCount);
            clock.Advance(TimeSpan.FromTicks(1));
            var second = await wire.NextJpeg();
            Assert.InRange(second - first, interval, interval + TimeSpan.FromMilliseconds(1));
            Assert.Equal(2, wire.JpegCount);
        }
        finally { stream.Abort(); await stream.Completion.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task Transport_forwards_the_effective_profile_cap_to_the_wire_pump()
    {
        var clock = new ManualClock();
        var wire = new ClockWire(clock, TimeSpan.Zero);
        await using var session = await Connect(wire, clock);
        D215BgraFrames? raw = null;
        using var transport = new D215StreamTransport(Port.Identity, _ => session,
            repeat => raw = new D215BgraFrames(4, _ => new EchoEncoder(), repeat), Deadline, framesPerSecond: 15);
        transport.BindRepeatGate(() => true);
        transport.Open();
        raw!.Queue([1, 2, 3, 4]);
        await Task.Run(transport.StartPlayer).WaitAsync(Deadline);
        var first = await wire.NextJpeg();
        var interval = TimeSpan.FromSeconds(1.0 / 15);
        var quantized = TimeSpan.FromMilliseconds(Math.Ceiling(interval.TotalMilliseconds));
        await WaitUntil(() => clock.HasTimerAt(quantized.Ticks));
        clock.Advance(quantized);
        Assert.Equal(quantized, await wire.NextJpeg() - first);
    }

    [Fact]
    public async Task Slow_delivery_reanchors_without_a_catch_up_burst_or_an_extra_idle_slot()
    {
        var clock = new ManualClock();
        var wire = new ClockWire(clock, TimeSpan.FromMilliseconds(50));
        await using var session = await Connect(wire, clock);
        var authorized = true;
        wire.AfterJpeg = count => { if (count == 2) authorized = false; };
        var stream = session.BeginStreaming(new FreshFrames(clock, TimeSpan.FromMilliseconds(2)), () => authorized, 60);
        await stream.Completion.WaitAsync(Deadline);
        var first = await wire.NextJpeg();
        var second = await wire.NextJpeg();
        Assert.Equal(TimeSpan.FromMilliseconds(52), second - first);
        Assert.Equal(2, wire.JpegCount);
        Assert.Null(stream.Failure);
    }

    [Fact]
    public async Task Encoding_across_a_renewal_deadline_renews_before_the_complete_frame()
    {
        var clock = new ManualClock();
        var wire = new ClockWire(clock, TimeSpan.Zero);
        await using var session = await Connect(wire, clock);
        var authorized = true;
        wire.AfterJpeg = _ => authorized = false;
        var stream = session.BeginStreaming(new FreshFrames(clock, TimeSpan.FromMilliseconds(1600)), () => authorized, 60);
        await stream.Completion.WaitAsync(Deadline);
        Assert.Equal(new[] { "clear", "clear", "info", "live", "live", "jpeg" }, wire.Operations);
        Assert.Null(stream.Failure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(61)]
    public void Invalid_effective_rates_are_rejected(int fps) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Timing.AtFrameRate(fps));

    [Fact]
    public void Unspecified_rate_preserves_an_explicit_test_or_session_timing() => Assert.Same(Timing, Timing.AtFrameRate(null));

    private static ValueTask<D215Session> Connect(ClockWire wire, ManualClock clock) =>
        D215Session.ConnectAsync(Port, new Discovery(), (_, _) => ValueTask.FromResult<ID215SerialTransport>(wire),
            clock, Timing, CancellationToken.None);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > Deadline) throw new TimeoutException("The pump did not schedule its next frame.");
            await Task.Yield();
        }
    }

    private sealed class Discovery : ID215PortDiscovery
    {
        public IReadOnlyList<D215Port> Discover() => [Port];
    }

    private sealed class EchoEncoder : ID215JpegEncoder
    {
        public ReadOnlySpan<byte> Encode(ReadOnlySpan<byte> frame) => frame;
        public void Dispose() { }
    }

    private sealed class FreshFrames(ManualClock clock, TimeSpan encode) : ID215FrameSource
    {
        public bool MayRepeat => true;
        public ReadOnlyMemory<byte> TakeLatestEnvelope()
        {
            clock.Advance(encode);
            return new byte[] { 4, 0, 0, 0, 0xFF, 0xD8, 0xFF, 0xD9, 0xB3, 3 };
        }
    }

    private sealed class ClockWire(ManualClock clock, TimeSpan write) : ID215SerialTransport
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<TimeSpan> _jpegs = Channel.CreateUnbounded<TimeSpan>();
        private int _jpegCount;
        internal List<string> Operations { get; } = [];
        internal Action<int>? AfterJpeg { get; set; }
        internal int JpegCount => Volatile.Read(ref _jpegCount);
        internal Task<TimeSpan> NextJpeg() => _jpegs.Reader.ReadAsync().AsTask().WaitAsync(Deadline);

        public ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hex = Convert.ToHexString(source.Span);
            if (hex is "FFD9FFD9" or "00000000") Operations.Add("clear");
            else if (hex == "55AA0700060C01")
            {
                Operations.Add("info");
                _replies.Writer.TryWrite(Info());
            }
            else if (hex == "55AA0700111701")
            {
                Operations.Add("live");
                _replies.Writer.TryWrite(Convert.FromHexString("55AA080011001801"));
            }
            else
            {
                Operations.Add("jpeg");
                var count = Interlocked.Increment(ref _jpegCount);
                _jpegs.Writer.TryWrite(clock.Elapsed);
                clock.Advance(write);
                AfterJpeg?.Invoke(count);
            }
            return ValueTask.CompletedTask;
        }

        public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            var reply = await _replies.Reader.ReadAsync(cancellationToken);
            reply.CopyTo(destination);
            return reply.Length;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static byte[] Info()
        {
            ReadOnlySpan<byte> payload = """{"cmd":"info","data":{"model":"D215-NOR-JD9365-6.67inch-hor","version":"3.4","brightness":80,"diplay_on":false}}"""u8;
            var reply = new byte[payload.Length + 7];
            reply[0] = 0x55; reply[1] = 0xAA; reply[4] = 6;
            BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(2), (ushort)reply.Length);
            payload.CopyTo(reply.AsSpan(5));
            int checksum = 0;
            foreach (var value in reply.AsSpan(0, reply.Length - 2)) checksum += value;
            BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(reply.Length - 2), (ushort)checksum);
            return reply;
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly object _lock = new();
        private readonly List<Timer> _timers = [];
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        internal TimeSpan Elapsed => TimeSpan.FromTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(callback, state, GetTimestamp() + dueTime.Ticks);
            lock (_lock) _timers.Add(timer);
            return timer;
        }
        internal bool HasTimerAt(long ticks)
        {
            lock (_lock) return _timers.Any(t => !t.Disposed && t.Due == ticks);
        }
        internal void Advance(TimeSpan elapsed)
        {
            var now = Interlocked.Add(ref _ticks, elapsed.Ticks);
            Timer[] due;
            lock (_lock)
            {
                due = _timers.Where(t => !t.Disposed && t.Due <= now).ToArray();
                _timers.RemoveAll(t => t.Disposed || t.Due <= now);
            }
            foreach (var timer in due) timer.Fire();
        }
        private sealed class Timer(TimerCallback callback, object? state, long due) : ITimer
        {
            internal bool Disposed;
            internal long Due { get; } = due;
            internal void Fire() { if (!Disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
