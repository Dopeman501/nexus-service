using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nexus.Service.Peripherals.JungleLeopard;

namespace Nexus.Service.Tests.JungleLeopard;

public sealed class D215LiveStreamTests
{
    private const string EnterLive = "55AA0700111701";
    private const string Frame = "04000000FFD8FFD9B303";
    private const string ClearMarker = "FFD9FFD9";
    private const string ClearLength = "00000000";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(4);
    private static readonly D215Timing Timing = D215Timing.Default with
    {
        ClearSettle = TimeSpan.Zero,
        FrameInterval = TimeSpan.FromMilliseconds(15),
        LiveRenewInterval = TimeSpan.FromMilliseconds(150),
    };

    [Theory]
    [InlineData(true, (int)D215ConnectionStatus.WriteTimedOut)]
    [InlineData(false, (int)D215ConnectionStatus.NoResponse)]
    public async Task A_driver_ignoring_cancellation_exposes_the_deadline_before_completion(bool write, int status)
    {
        var transport = new FakeTransport { BlockFrame = true, HoldAfterCancellation = true };
        var timing = Timing with { WriteTimeout = TimeSpan.FromMilliseconds(100), ReplyTimeout = TimeSpan.FromMilliseconds(100) };
        var wire = new D215Wire(transport, TimeProvider.System, timing);
        var operation = write
            ? wire.WriteAsync(Convert.FromHexString(Frame), CancellationToken.None).AsTask()
            : wire.EnterLiveAsync(CancellationToken.None).AsTask();
        try
        {
            await transport.CancellationObserved.Task.WaitAsync(TestTimeout);
            Assert.Equal((D215ConnectionStatus)status, wire.TimedOutOperation);
            Assert.False(operation.IsCompleted);
        }
        finally
        {
            transport.ReleaseFrame.TrySetResult();
            await Assert.ThrowsAsync<D215ConnectionException>(() => operation.WaitAsync(TestTimeout));
        }
        Assert.Null(wire.TimedOutOperation);
    }

    [Fact]
    public async Task Sends_only_live_request_until_split_ack_and_clears_on_stop()
    {
        var transport = new FakeTransport();
        var source = new FakeFrames { Repeat = false };
        var stream = Start(transport, source);
        try
        {
            Assert.Equal(EnterLive, await transport.NextWrite());
            Assert.False(stream.Started.IsCompleted);
            transport.Reply("55AA070022280155AA08");
            transport.Reply("0011001801");
            await stream.Started.WaitAsync(TestTimeout);
            Assert.Equal(Frame, await transport.NextWrite());

            stream.RequestStop();
            Assert.Equal(ClearMarker, await transport.NextWrite());
            Assert.Equal(ClearLength, await transport.NextWrite());
            await stream.Completion.WaitAsync(TestTimeout);
            Assert.Null(stream.Failure);
        }
        finally { await Abort(stream); }
    }

    [Fact]
    public async Task Renews_between_complete_repeated_frames_without_waiting_for_a_reply()
    {
        var transport = new FakeTransport();
        var source = new FakeFrames { Repeat = true };
        var stream = Start(transport, source);
        try
        {
            Assert.Equal(EnterLive, await transport.NextWrite());
            transport.Reply("55AA080011001801");
            await stream.Started.WaitAsync(TestTimeout);
            Assert.Equal(Frame, await transport.NextWrite());
            Assert.Equal(Frame, await transport.NextWrite());
            string next;
            do { next = await transport.NextWrite(); } while (next == Frame);
            Assert.Equal(EnterLive, next);
            Assert.Equal(Frame, await transport.NextWrite());
        }
        finally { await Abort(stream); }
    }

    [Fact]
    public async Task Continues_to_renew_when_coordinator_disallows_repeats()
    {
        var transport = new FakeTransport();
        var stream = Start(transport, new FakeFrames { Repeat = false });
        try
        {
            Assert.Equal(EnterLive, await transport.NextWrite());
            transport.Reply("55AA080011001801");
            Assert.Equal(Frame, await transport.NextWrite());
            Assert.Equal(EnterLive, await transport.NextWrite());
            Assert.Equal(EnterLive, await transport.NextWrite());
        }
        finally { await Abort(stream); }
    }

    [Fact]
    public async Task Stop_waits_for_the_in_flight_envelope_before_clearing()
    {
        var transport = new FakeTransport { BlockFrame = true };
        var stream = Start(transport, new FakeFrames());
        try
        {
            Assert.Equal(EnterLive, await transport.NextWrite());
            transport.Reply("55AA080011001801");
            Assert.Equal(Frame, await transport.NextWrite());
            stream.RequestStop();
            Assert.False(stream.Completion.IsCompleted);
            Assert.False(transport.HasWrite);

            transport.ReleaseFrame.TrySetResult();
            Assert.Equal(ClearMarker, await transport.NextWrite());
            Assert.Equal(ClearLength, await transport.NextWrite());
            await stream.Completion.WaitAsync(TestTimeout);
            Assert.Null(stream.Failure);
        }
        finally { await Abort(stream); }
    }

    [Fact]
    public async Task Only_a_retiring_stream_exposes_its_completion_to_the_next_owner()
    {
        var transport = new FakeTransport { BlockFrame = true };
        var port = new D215Port("COM7", "test-device", true);
        var timing = Timing with { PortSettle = TimeSpan.Zero };
        var connect = D215Session.ConnectAsync(port, new SinglePortDiscovery(port),
            (_, _) => ValueTask.FromResult<ID215SerialTransport>(transport), TimeProvider.System,
            timing, CancellationToken.None).AsTask();
        await ExpectClearThenInfo(transport);
        transport.Reply(InfoFrame(80));
        await using var session = await connect.WaitAsync(TestTimeout);
        Assert.Null(session.GetRetiringStreamCompletion());

        var stream = session.BeginStreaming(new FakeFrames(), () => true);
        try
        {
            await StartAndAwaitFrame(transport, stream);
            Assert.Null(session.GetRetiringStreamCompletion());
            Assert.Throws<InvalidOperationException>(() => session.BeginStreaming(new FakeFrames(), () => true));

            stream.RequestStop();
            Assert.Same(stream.Completion, session.GetRetiringStreamCompletion());
            Assert.False(stream.Completion.IsCompleted);
            transport.ReleaseFrame.TrySetResult();
            Assert.Equal(ClearMarker, await transport.NextWrite());
            Assert.Equal(ClearLength, await transport.NextWrite());
            await stream.Completion.WaitAsync(TestTimeout);
            Assert.Null(session.GetRetiringStreamCompletion());
        }
        finally { await Abort(stream); }
    }

    [Fact]
    public async Task Detach_cancels_the_in_flight_write_without_sending_clear()
    {
        var transport = new FakeTransport { BlockFrame = true };
        var stream = Start(transport, new FakeFrames());
        Assert.Equal(EnterLive, await transport.NextWrite());
        transport.Reply("55AA080011001801");
        Assert.Equal(Frame, await transport.NextWrite());

        await Abort(stream);

        Assert.True(transport.WriteCancelled);
        Assert.False(transport.HasWrite);
    }

    [Fact]
    public async Task Generation_loss_without_a_graceful_stop_does_not_clear_the_wire()
    {
        var transport = new FakeTransport { BlockFrame = true };
        int authorized = 1;
        var stream = new D215LiveStream(new D215Wire(transport, TimeProvider.System, Timing), new FakeFrames(),
            () => Volatile.Read(ref authorized) != 0, TimeProvider.System, Timing);
        try
        {
            Assert.Equal(EnterLive, await transport.NextWrite());
            transport.Reply("55AA080011001801");
            Assert.Equal(Frame, await transport.NextWrite());
            Volatile.Write(ref authorized, 0);
            transport.ReleaseFrame.TrySetResult();

            await stream.Completion.WaitAsync(TestTimeout);
            Assert.False(transport.HasWrite);
        }
        finally { await Abort(stream); }
    }

    [Fact]
    public async Task Invalid_live_ack_faults_without_sending_a_frame_or_another_command()
    {
        var transport = new FakeTransport();
        var stream = Start(transport, new FakeFrames());
        try
        {
            Assert.Equal(EnterLive, await transport.NextWrite());
            transport.Reply("55AA080011011901");

            await Assert.ThrowsAsync<D215ConnectionException>(() => stream.Started.WaitAsync(TestTimeout));
            await stream.Completion.WaitAsync(TestTimeout);
            Assert.IsType<D215ConnectionException>(stream.Failure);
            Assert.False(transport.HasWrite);
        }
        finally { await Abort(stream); }
    }

    [Theory]
    [InlineData(20, 20, "confirmed", "55AA080003141E01")]
    [InlineData(40, 40, "confirmed", "55AA080003283201")]
    [InlineData(60, 60, "confirmed", "55AA0800033C4601")]
    [InlineData(80, 80, "confirmed", "55AA080003505A01")]
    [InlineData(100, 100, "confirmed", "55AA080003646E01")]
    [InlineData(100, 80, "mismatch", "55AA080003646E01")]
    public async Task Brightness_pauses_packets_reports_readback_and_resumes_live(int value, int readback, string status, string request)
    {
        var transport = new FakeTransport { BlockFrame = true };
        var confirmed = new List<int?>();
        var stream = StartBrightnessStream(transport, info => confirmed.Add(info?.Brightness));
        try
        {
            await StartAndAwaitFrame(transport, stream);
            var result = stream.SetBrightnessAsync(value, () => true, CancellationToken.None).AsTask();
            var busy = await stream.SetBrightnessAsync(80, () => true, CancellationToken.None);
            Assert.Equal("busy", busy.Status);
            Assert.False(transport.HasWrite);
            transport.ReleaseFrame.TrySetResult();

            await ExpectClearThenInfo(transport);
            var before = value == 80 ? 100 : 80;
            transport.Reply(InfoFrame(before));
            Assert.Equal(request, await transport.NextWrite());
            transport.Reply("55AA080003000A01");
            Assert.Equal("55AA0700060C01", await transport.NextWrite());
            Assert.False(result.IsCompleted);
            transport.Reply(InfoFrame(readback));

            var outcome = await result.WaitAsync(TestTimeout);
            Assert.Equal(status, outcome.Status);
            Assert.Equal(readback, outcome.Value);
            Assert.Equal(new int?[] { before, null, readback }, confirmed);
            Assert.Equal(EnterLive, await transport.NextWrite());
            transport.Reply("55AA080011001801");
        }
        finally { await Abort(stream); }
    }

    [Fact]
    public async Task Bad_brightness_ack_leaves_value_unknown_and_invalidates_the_stream()
    {
        var transport = new FakeTransport { BlockFrame = true };
        var confirmed = new List<int?>();
        var stream = StartBrightnessStream(transport, info => confirmed.Add(info?.Brightness));
        try
        {
            await StartAndAwaitFrame(transport, stream);
            var result = stream.SetBrightnessAsync(100, () => true, CancellationToken.None).AsTask();
            transport.ReleaseFrame.TrySetResult();
            await ExpectClearThenInfo(transport);
            transport.Reply(InfoFrame(80));
            Assert.Equal("55AA080003646E01", await transport.NextWrite());
            transport.Reply("55AA080003010B01");

            var outcome = await result.WaitAsync(TestTimeout);
            Assert.Equal("failed", outcome.Status);
            Assert.Null(outcome.Value);
            await stream.Completion.WaitAsync(TestTimeout);
            Assert.IsType<D215ConnectionException>(stream.Failure);
            Assert.Equal(new int?[] { 80, null }, confirmed);
            Assert.False(transport.HasWrite);
        }
        finally { await Abort(stream); }
    }

    [Fact]
    public async Task Revoked_panel_record_prevents_queued_brightness_from_writing()
    {
        var transport = new FakeTransport { BlockFrame = true };
        var stream = StartBrightnessStream(transport, _ => { });
        try
        {
            await StartAndAwaitFrame(transport, stream);
            int valid = 1;
            var result = stream.SetBrightnessAsync(100, () => Volatile.Read(ref valid) == 1, CancellationToken.None).AsTask();
            Volatile.Write(ref valid, 0);
            transport.ReleaseFrame.TrySetResult();
            Assert.Equal("unavailable", (await result.WaitAsync(TestTimeout)).Status);
            Assert.False(transport.HasWrite);
        }
        finally { await Abort(stream); }
    }

    [Fact]
    public async Task Caller_cancellation_during_preflight_prevents_the_brightness_write()
    {
        var transport = new FakeTransport { BlockFrame = true };
        var stream = StartBrightnessStream(transport, _ => { });
        using var caller = new CancellationTokenSource();
        try
        {
            await StartAndAwaitFrame(transport, stream);
            var result = stream.SetBrightnessAsync(100, () => true, caller.Token).AsTask();
            transport.ReleaseFrame.TrySetResult();
            await ExpectClearThenInfo(transport);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result);
            transport.Reply(InfoFrame(80));
            Assert.Equal(EnterLive, await transport.NextWrite());
            transport.Reply("55AA080011001801");
        }
        finally { await Abort(stream); }
    }

    [Fact]
    public async Task Stop_at_brightness_dispatch_clears_live_mode()
    {
        var transport = new FakeTransport { BlockFrame = true };
        var stream = StartBrightnessStream(transport, _ => { });
        try
        {
            await StartAndAwaitFrame(transport, stream);
            int checks = 0;
            var result = stream.SetBrightnessAsync(100, () =>
            {
                if (Interlocked.Increment(ref checks) == 2) stream.RequestStop();
                return true;
            }, CancellationToken.None).AsTask();
            transport.ReleaseFrame.TrySetResult();

            Assert.Equal("unavailable", (await result.WaitAsync(TestTimeout)).Status);
            Assert.Equal(ClearMarker, await transport.NextWrite());
            Assert.Equal(ClearLength, await transport.NextWrite());
            await stream.Completion.WaitAsync(TestTimeout);
            Assert.False(transport.HasWrite);
        }
        finally { await Abort(stream); }
    }

    private static D215LiveStream StartBrightnessStream(FakeTransport transport, Action<D215DeviceInfo?> remember)
    {
        var timing = Timing with { LiveRenewInterval = TimeSpan.FromSeconds(2) };
        return new D215LiveStream(new D215Wire(transport, TimeProvider.System, timing), new FakeFrames(),
            () => true, TimeProvider.System, timing, remember);
    }

    private static async Task StartAndAwaitFrame(FakeTransport transport, D215LiveStream stream)
    {
        Assert.Equal(EnterLive, await transport.NextWrite());
        transport.Reply("55AA080011001801");
        await stream.Started.WaitAsync(TestTimeout);
        Assert.Equal(Frame, await transport.NextWrite());
    }

    private static async Task ExpectClearThenInfo(FakeTransport transport)
    {
        Assert.Equal(ClearMarker, await transport.NextWrite());
        Assert.Equal(ClearLength, await transport.NextWrite());
        Assert.Equal("55AA0700060C01", await transport.NextWrite());
    }

    private static string InfoFrame(int brightness)
    {
        var payload = Encoding.UTF8.GetBytes($$"""{"cmd":"info","data":{"model":"D215-NOR-JD9365-6.67inch-hor","version":"3.4","brightness":{{brightness}},"diplay_on":false} }""");
        var frame = new byte[payload.Length + 7];
        frame[0] = 0x55;
        frame[1] = 0xAA;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), (ushort)frame.Length);
        frame[4] = 0x06;
        payload.CopyTo(frame, 5);
        int sum = 0;
        foreach (var value in frame.AsSpan(0, frame.Length - 2)) sum += value;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(frame.Length - 2), unchecked((ushort)sum));
        return Convert.ToHexString(frame);
    }

    private static D215LiveStream Start(FakeTransport transport, FakeFrames source) =>
        new(new D215Wire(transport, TimeProvider.System, Timing), source, () => true, TimeProvider.System, Timing);

    private static async Task Abort(D215LiveStream stream)
    {
        stream.Abort();
        await stream.Completion.WaitAsync(TestTimeout);
    }

    private sealed class SinglePortDiscovery(D215Port port) : ID215PortDiscovery
    {
        public IReadOnlyList<D215Port> Discover() => [port];
    }

    private sealed class FakeFrames : ID215FrameSource
    {
        private byte[]? _frame = Convert.FromHexString(Frame);
        internal bool Repeat { get; init; }
        public bool MayRepeat => Repeat;
        public ReadOnlyMemory<byte> TakeLatestEnvelope() => Interlocked.Exchange(ref _frame, null);
    }

    private sealed class FakeTransport : ID215SerialTransport
    {
        private readonly Channel<string> _writes = Channel.CreateUnbounded<string>();
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        internal bool BlockFrame { get; init; }
        internal bool HoldAfterCancellation { get; init; }
        internal bool WriteCancelled { get; private set; }
        internal bool HasWrite => _writes.Reader.TryPeek(out _);
        internal TaskCompletionSource ReleaseFrame { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task<string> NextWrite() => _writes.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);
        internal void Reply(string hex) => Assert.True(_replies.Writer.TryWrite(Convert.FromHexString(hex)));

        public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            if (HoldAfterCancellation)
            {
                await HoldAsync(cancellationToken);
                return 0;
            }
            var data = await _replies.Reader.ReadAsync(cancellationToken);
            data.AsSpan().CopyTo(destination.Span);
            return data.Length;
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packet = Convert.ToHexString(source.Span);
            Assert.True(_writes.Writer.TryWrite(packet));
            if (BlockFrame && packet == Frame)
            {
                if (HoldAfterCancellation)
                {
                    await HoldAsync(cancellationToken);
                    return;
                }
                try { await ReleaseFrame.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { WriteCancelled = true; throw; }
            }
        }

        private async Task HoldAsync(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() => CancellationObserved.TrySetResult());
            await ReleaseFrame.Task;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
