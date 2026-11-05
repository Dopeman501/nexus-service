using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices;
using Nexus.Service.Panel;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.JungleLeopard;

namespace Nexus.Service.Tests.JungleLeopard;

public sealed class D215SessionTests
{
    private const string JpegEnvelope = "04000000FFD8FFD9B303";
    private static readonly D215Port Port = new("COM7", "test-device", true);
    private static readonly D215Timing Timing = D215Timing.Default with
    {
        PortSettle = TimeSpan.Zero,
        ClearSettle = TimeSpan.Zero,
    };

    [Fact]
    public async Task Identifies_after_ordered_clear_and_ignores_unsolicited_reply()
    {
        var info = InfoFrame();
        var wire = new FakeWire(Convert.FromHexString("55AA0700222801"), info[..11], info[11..]);
        var session = await Connect(wire);

        Assert.Equal("3.4", session.Info.Firmware);
        Assert.Equal(80, session.Info.Brightness);
        Assert.Equal(new[] { "FFD9FFD9", "00000000", "55AA0700060C01" }, wire.Writes);
        await session.DisposeAsync();
        await session.DisposeAsync();
        Assert.Equal(1, wire.DisposeCount);
    }

    [Theory]
    [InlineData("3.6", (int)D215ConnectionStatus.Unsupported)]
    [InlineData("", (int)D215ConnectionStatus.InvalidReply)]
    public async Task Incompatible_or_invalid_info_releases_port_without_control_writes(string version, int status)
    {
        var wire = new FakeWire(InfoFrame(version));

        var failure = await Assert.ThrowsAsync<D215ConnectionException>(() => Connect(wire).AsTask());

        Assert.Equal((D215ConnectionStatus)status, failure.Status);
        Assert.Equal(new[] { "FFD9FFD9", "00000000", "55AA0700060C01" }, wire.Writes);
        Assert.Equal(1, wire.DisposeCount);
    }

    [Fact]
    public async Task Reassigned_port_is_closed_before_any_write()
    {
        var wire = new FakeWire();
        var discovery = new FakeDiscovery { Ports = [Port with { Identity = "different-device" }] };

        var failure = await Assert.ThrowsAsync<D215ConnectionException>(() => Connect(wire, discovery).AsTask());

        Assert.Equal(D215ConnectionStatus.PortChanged, failure.Status);
        Assert.Empty(wire.Writes);
        Assert.Equal(1, wire.DisposeCount);
    }

    [Fact]
    public async Task Reply_timeout_cancels_the_reader_and_discards_the_session()
    {
        var wire = new FakeWire();
        var clock = new ManualClock();
        var attempt = Connect(wire, clock: clock).AsTask();
        await wire.WaitingForRead.Task.WaitAsync(TimeSpan.FromSeconds(3));

        clock.Advance(Timing.ReplyTimeout);

        var failure = await Assert.ThrowsAsync<D215ConnectionException>(() => attempt);
        Assert.Equal(D215ConnectionStatus.NoResponse, failure.Status);
        Assert.True(wire.ReadWasCancelled);
        Assert.Equal(1, wire.DisposeCount);
    }

    [Fact]
    public async Task Caller_cancellation_after_open_cleans_up_the_pending_reader()
    {
        var wire = new FakeWire();
        using var stop = new CancellationTokenSource();
        var attempt = Connect(wire, cancellationToken: stop.Token).AsTask();
        await wire.WaitingForRead.Task.WaitAsync(TimeSpan.FromSeconds(3));

        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
        Assert.True(wire.ReadWasCancelled);
        Assert.Equal(1, wire.DisposeCount);
    }

    [Fact]
    public async Task Open_deadline_keeps_the_lease_until_a_late_handle_is_closed()
    {
        var clock = new ManualClock();
        var wire = new FakeWire();
        var late = new TaskCompletionSource<ID215SerialTransport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempt = D215Session.ConnectAsync(Port, new FakeDiscovery { Ports = [Port] },
            (_, _) => new ValueTask<ID215SerialTransport>(late.Task), clock, Timing, CancellationToken.None).AsTask();

        clock.Advance(Timing.OpenTimeout);
        Assert.False(attempt.IsCompleted);
        late.SetResult(wire);

        var failure = await Assert.ThrowsAsync<D215ConnectionException>(() => attempt);
        Assert.Equal(D215ConnectionStatus.OpenTimedOut, failure.Status);
        Assert.Empty(wire.Writes);
        Assert.Equal(1, wire.DisposeCount);
    }

    [Fact]
    public async Task Stalled_write_cancels_without_sending_the_next_command()
    {
        var clock = new ManualClock();
        var wire = new FakeWire { BlockFirstWrite = true };
        var attempt = Connect(wire, clock: clock).AsTask();
        await wire.WaitingForWrite.Task.WaitAsync(TimeSpan.FromSeconds(3));

        clock.Advance(Timing.WriteTimeout);

        var failure = await Assert.ThrowsAsync<D215ConnectionException>(() => attempt);
        Assert.Equal(D215ConnectionStatus.WriteTimedOut, failure.Status);
        Assert.Equal(new[] { "FFD9FFD9" }, wire.Writes);
        Assert.Equal(1, wire.DisposeCount);
    }

    [Fact]
    public async Task Failure_to_release_after_bad_info_preserves_both_failures()
    {
        var wire = new FakeWire(InfoFrame("3.6")) { FailDispose = true };
        var failure = await Assert.ThrowsAsync<D215ConnectionException>(() => Connect(wire).AsTask());

        Assert.Equal(D215ConnectionStatus.CleanupFailed, failure.Status);
        var errors = Assert.IsType<AggregateException>(failure.InnerException).InnerExceptions;
        Assert.Equal(D215ConnectionStatus.Unsupported, Assert.IsType<D215ConnectionException>(errors[0]).Status);
        Assert.IsType<IOException>(errors[1]);
        Assert.Equal(1, wire.DisposeCount);
    }

    [Fact]
    public async Task Worker_waits_for_opt_in_and_releases_on_disable_and_detach()
    {
        var config = new InMemoryConfigStore();
        var gate = new DeviceControlGate(config);
        var discovery = new FakeDiscovery { Ports = [Port] };
        var wires = new Queue<FakeWire>([new(InfoFrame()), new(InfoFrame())]);
        var first = wires.Peek();
        int opens = 0;
        using var worker = new D215ConnectionWorker(discovery, gate, (_, _) =>
        {
            Interlocked.Increment(ref opens);
            return ValueTask.FromResult<ID215SerialTransport>(wires.Dequeue());
        }, TimeProvider.System, Timing, TimeSpan.FromMilliseconds(10));
        var store = new StreamedPanelStore(Path.Combine(Path.GetTempPath(), $"nexus-d215-discovery-{Guid.NewGuid():N}.json"));
        var panels = new D215PanelDiscovery(worker, store, new PanelDeviceRegistry(config));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(stop.Token);
        try
        {
            await WaitFor(worker, D215ConnectionStatus.Available, stop.Token);
            Assert.Equal(0, Volatile.Read(ref opens));
            Assert.Empty(panels.Discover());

            gate.SetEnabled(D215ConnectionWorker.HandlerId, true);
            await WaitFor(worker, D215ConnectionStatus.Identified, stop.Token);
            Assert.Equal(1, Volatile.Read(ref opens));
            var panel = Assert.Single(panels.Discover());
            Assert.Equal(Port.Identity, panel.Serial);
            Assert.Equal("lcd-portrait", panel.Profile.Surface);
            Assert.Equal((756, 1680, 60, 1), (panel.Profile.CssWidth, panel.Profile.CssHeight, panel.Profile.Fps, panel.Profile.WriteBatchFrames));
            Assert.True(panel.Profile.RequireNativeRawFrames);
            Assert.True(panel.Profile.KeepLatestRawFrame);
            Assert.False(panel.Profile.SupportsBrightness);
            using var transport = panels.CreateTransport(panel);
            Assert.Equal(panel.Serial, transport.Serial);

            gate.SetEnabled(D215ConnectionWorker.HandlerId, false);
            await WaitFor(worker, D215ConnectionStatus.Available, stop.Token);
            Assert.Equal(1, first.DisposeCount);
            Assert.Empty(panels.Discover());

            gate.SetEnabled(D215ConnectionWorker.HandlerId, true);
            await WaitFor(worker, D215ConnectionStatus.Identified, stop.Token);
            discovery.Ports = [];
            await WaitFor(worker, D215ConnectionStatus.Absent, stop.Token);
            Assert.Equal(2, Volatile.Read(ref opens));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Late_open_retains_ownership_until_it_has_been_closed()
    {
        var gate = new DeviceControlGate(new InMemoryConfigStore());
        gate.SetEnabled(D215ConnectionWorker.HandlerId, true);
        var discovery = new FakeDiscovery { Ports = [Port] };
        var first = new FakeWire();
        var second = new FakeWire(InfoFrame());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<ID215SerialTransport>(TaskCreationOptions.RunContinuationsAsynchronously);
        int opens = 0;
        var timing = Timing with { ReleaseTimeout = TimeSpan.FromMilliseconds(150) };
        using var worker = new D215ConnectionWorker(discovery, gate, async (_, token) =>
        {
            if (Interlocked.Increment(ref opens) != 1)
            {
                Assert.Equal(1, first.DisposeCount);
                return second;
            }
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            return await late.Task;
        }, TimeProvider.System, timing, TimeSpan.FromMilliseconds(10));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(stop.Token);
        try
        {
            await entered.Task.WaitAsync(stop.Token);
            gate.SetEnabled(D215ConnectionWorker.HandlerId, false);
            await cancelled.Task.WaitAsync(stop.Token);
            gate.SetEnabled(D215ConnectionWorker.HandlerId, true);
            await WaitFor(worker, D215ConnectionStatus.ReleaseTimedOut, stop.Token);
            Assert.Equal(Port, worker.Snapshot.Port);
            Assert.Null(worker.Snapshot.Info);
            Assert.Equal(1, Volatile.Read(ref opens));

            late.TrySetResult(first);
            await WaitFor(worker, D215ConnectionStatus.Identified, stop.Token);
            Assert.Equal(2, Volatile.Read(ref opens));
            Assert.Empty(first.Writes);
            Assert.Equal(1, first.DisposeCount);
        }
        finally
        {
            late.TrySetResult(first);
            await worker.StopAsync(CancellationToken.None);
        }
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public async Task Held_close_allows_discovery_but_blocks_reclaim_until_close_finishes()
    {
        var gate = new DeviceControlGate(new InMemoryConfigStore());
        gate.SetEnabled(D215ConnectionWorker.HandlerId, true);
        var discovery = new FakeDiscovery { Ports = [Port] };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new FakeWire(InfoFrame()) { HoldClose = release.Task };
        var second = new FakeWire(InfoFrame());
        var moved = Port with { PortName = "COM8" };
        var timing = Timing with { ReleaseTimeout = TimeSpan.FromSeconds(1) };
        var reopened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int opens = 0;
        using var worker = new D215ConnectionWorker(discovery, gate, (_, _) =>
        {
            if (Interlocked.Increment(ref opens) == 1) return ValueTask.FromResult<ID215SerialTransport>(first);
            reopened.TrySetResult(first.Closed.Task.IsCompletedSuccessfully);
            return ValueTask.FromResult<ID215SerialTransport>(second);
        }, TimeProvider.System, timing, TimeSpan.FromMilliseconds(10));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(stop.Token);
        try
        {
            await WaitFor(worker, D215ConnectionStatus.Identified, stop.Token);
            gate.SetEnabled(D215ConnectionWorker.HandlerId, false);
            await first.Closing.Task.WaitAsync(stop.Token);
            await WaitFor(worker, D215ConnectionStatus.Releasing, stop.Token);
            discovery.Ports = [moved];
            gate.SetEnabled(D215ConnectionWorker.HandlerId, true);
            while (worker.Snapshot.Port != moved)
                await Task.Delay(5, stop.Token);
            await WaitFor(worker, D215ConnectionStatus.ReleaseTimedOut, stop.Token);
            Assert.Null(worker.Snapshot.Info);
            Assert.False(reopened.Task.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref opens));

            release.TrySetResult();
            Assert.True(await reopened.Task.WaitAsync(stop.Token));
            await WaitFor(worker, D215ConnectionStatus.Identified, stop.Token);
            Assert.Equal(moved, worker.Snapshot.Port);
            Assert.Equal(1, first.DisposeCount);
            Assert.Equal(2, Volatile.Read(ref opens));
        }
        finally
        {
            release.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Held_live_write_retires_the_lease_before_reclaiming_a_moved_device()
    {
        var gate = new DeviceControlGate(new InMemoryConfigStore());
        gate.SetEnabled(D215ConnectionWorker.HandlerId, true);
        var discovery = new FakeDiscovery { Ports = [Port] };
        var clock = new ManualClock();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new FakeWire(InfoFrame(), Convert.FromHexString("55AA080011001801")) { HoldLiveFrame = release.Task };
        var second = new FakeWire(InfoFrame(brightness: 100));
        var reopened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var moved = Port with { PortName = "COM8" };
        int opens = 0;
        using var worker = new D215ConnectionWorker(discovery, gate, (_, _) =>
        {
            if (Interlocked.Increment(ref opens) == 1) return ValueTask.FromResult<ID215SerialTransport>(first);
            reopened.TrySetResult(first.Closed.Task.IsCompletedSuccessfully);
            return ValueTask.FromResult<ID215SerialTransport>(second);
        }, clock, Timing, TimeSpan.FromMilliseconds(10));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(stop.Token);
        try
        {
            await WaitFor(worker, D215ConnectionStatus.Identified, stop.Token);
            var old = Assert.IsType<D215Session>(worker.GetIdentifiedSession(Port.Identity));
            var stream = old.BeginStreaming(new SingleFrame(), () => ReferenceEquals(old, worker.GetIdentifiedSession(Port.Identity)));
            await stream.Started.WaitAsync(stop.Token);
            await first.WaitingForWrite.Task.WaitAsync(stop.Token);
            clock.Advance(Timing.WriteTimeout);
            await first.WriteCancellationObserved.Task.WaitAsync(stop.Token);
            await WaitFor(worker, D215ConnectionStatus.WriteTimedOut, stop.Token);
            Assert.Null(worker.Snapshot.Info);
            Assert.Null(worker.GetIdentifiedSession(Port.Identity));

            discovery.Ports = [];
            while (worker.Snapshot.Port is not null) await Task.Delay(5, stop.Token);
            discovery.Ports = [moved];
            while (worker.Snapshot.Port != moved) await Task.Delay(5, stop.Token);
            clock.Advance(Timing.ReleaseTimeout);
            await WaitFor(worker, D215ConnectionStatus.ReleaseTimedOut, stop.Token);
            Assert.False(stream.Completion.IsCompleted);
            Assert.False(first.Closing.Task.IsCompleted);
            Assert.False(reopened.Task.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref opens));

            release.SetResult();
            Assert.True(await reopened.Task.WaitAsync(stop.Token));
            await WaitFor(worker, D215ConnectionStatus.Identified, stop.Token);
            var fresh = Assert.IsType<D215Session>(worker.GetIdentifiedSession(Port.Identity));
            Assert.NotSame(old, fresh);
            Assert.Equal(moved, fresh.Port);
            Assert.Equal(100, fresh.Info.Brightness);
            Assert.Equal(2, Volatile.Read(ref opens));
            Assert.Equal(new[] { "FFD9FFD9", "00000000", "55AA0700060C01", "55AA0700111701", JpegEnvelope }, first.Writes);
            Assert.Equal(new[] { "FFD9FFD9", "00000000", "55AA0700060C01" }, second.Writes);
        }
        finally
        {
            release.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Stop_is_bounded_and_a_late_open_is_closed_without_reclaim()
    {
        var gate = new DeviceControlGate(new InMemoryConfigStore());
        gate.SetEnabled(D215ConnectionWorker.HandlerId, true);
        var discovery = new FakeDiscovery { Ports = [Port] };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<ID215SerialTransport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wire = new FakeWire(InfoFrame());
        var timing = Timing with { ReleaseTimeout = TimeSpan.FromMilliseconds(150) };
        int opens = 0;
        using var worker = new D215ConnectionWorker(discovery, gate, (_, _) =>
        {
            Interlocked.Increment(ref opens);
            entered.TrySetResult();
            return new ValueTask<ID215SerialTransport>(late.Task);
        }, TimeProvider.System, timing, TimeSpan.FromMilliseconds(10));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(stop.Token);
        Task? stopped = null;
        try
        {
            await entered.Task.WaitAsync(stop.Token);
            stopped = worker.StopAsync(CancellationToken.None);
            await stopped.WaitAsync(stop.Token);
            Assert.False(wire.Closing.Task.IsCompleted);
            Assert.Null(worker.Snapshot.Info);
            Assert.Equal(1, Volatile.Read(ref opens));

            late.TrySetResult(wire);
            await wire.Closed.Task.WaitAsync(stop.Token);
            Assert.Empty(wire.Writes);
            Assert.Equal(1, wire.DisposeCount);
            Assert.Equal(1, Volatile.Read(ref opens));
        }
        finally
        {
            late.TrySetResult(wire);
            await (stopped ?? worker.StopAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task Unchanged_port_reports_overdue_identification_while_open_is_held()
    {
        var gate = new DeviceControlGate(new InMemoryConfigStore());
        gate.SetEnabled(D215ConnectionWorker.HandlerId, true);
        var discovery = new FakeDiscovery { Ports = [Port] };
        var late = new TaskCompletionSource<ID215SerialTransport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wire = new FakeWire(InfoFrame());
        int opens = 0;
        var timing = Timing with
        {
            OpenTimeout = TimeSpan.FromMilliseconds(20),
            WriteTimeout = TimeSpan.FromMilliseconds(20),
            ReplyTimeout = TimeSpan.FromMilliseconds(20),
        };
        using var worker = new D215ConnectionWorker(discovery, gate, (_, _) =>
        {
            Interlocked.Increment(ref opens);
            return new ValueTask<ID215SerialTransport>(late.Task);
        }, TimeProvider.System, timing, TimeSpan.FromMilliseconds(10));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await worker.StartAsync(stop.Token);
        try
        {
            await WaitFor(worker, D215ConnectionStatus.NoResponse, stop.Token);
            Assert.Equal(Port, worker.Snapshot.Port);
            Assert.Null(worker.Snapshot.Info);
            Assert.Equal(1, Volatile.Read(ref opens));
        }
        finally
        {
            late.TrySetResult(wire);
            await worker.StopAsync(CancellationToken.None);
        }
        Assert.Empty(wire.Writes);
        Assert.Equal(1, wire.DisposeCount);
    }

    [Fact]
    public async Task Busy_port_retries_are_bounded_until_an_explicit_retry()
    {
        var gate = new DeviceControlGate(new InMemoryConfigStore());
        gate.SetEnabled(D215ConnectionWorker.HandlerId, true);
        var discovery = new FakeDiscovery { Ports = [Port] };
        int opens = 0;
        using var worker = new D215ConnectionWorker(discovery, gate, (_, _) =>
        {
            Interlocked.Increment(ref opens);
            throw new UnauthorizedAccessException();
        }, TimeProvider.System, Timing, TimeSpan.FromMilliseconds(10));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(stop.Token);
        try
        {
            while (Volatile.Read(ref opens) < 2) await Task.Delay(5, stop.Token);
            await WaitFor(worker, D215ConnectionStatus.Busy, stop.Token);
            Assert.Contains("tray", worker.Snapshot.Warning);
            await Task.Delay(50, stop.Token);
            Assert.Equal(2, Volatile.Read(ref opens));

            gate.SetEnabled(D215ConnectionWorker.HandlerId, true);
            while (Volatile.Read(ref opens) < 3) await Task.Delay(5, stop.Token);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Detach_cancels_identification_and_releases_the_port()
    {
        var gate = new DeviceControlGate(new InMemoryConfigStore());
        gate.SetEnabled(D215ConnectionWorker.HandlerId, true);
        var discovery = new FakeDiscovery { Ports = [Port] };
        var wire = new FakeWire();
        using var worker = new D215ConnectionWorker(discovery, gate, (_, _) => ValueTask.FromResult<ID215SerialTransport>(wire),
            TimeProvider.System, Timing, TimeSpan.FromMilliseconds(10));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(stop.Token);
        try
        {
            await wire.WaitingForRead.Task.WaitAsync(stop.Token);
            discovery.Ports = [];
            await WaitFor(worker, D215ConnectionStatus.Absent, stop.Token);
            Assert.True(wire.ReadWasCancelled);
            Assert.Equal(1, wire.DisposeCount);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Failed_release_quarantines_the_lease_across_an_explicit_retry()
    {
        var gate = new DeviceControlGate(new InMemoryConfigStore());
        gate.SetEnabled(D215ConnectionWorker.HandlerId, true);
        var discovery = new FakeDiscovery { Ports = [Port] };
        var wire = new FakeWire(InfoFrame()) { FailDispose = true };
        int opens = 0;
        using var worker = new D215ConnectionWorker(discovery, gate, (_, _) =>
        {
            Interlocked.Increment(ref opens);
            return ValueTask.FromResult<ID215SerialTransport>(wire);
        }, TimeProvider.System, Timing, TimeSpan.FromMilliseconds(10));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(stop.Token);
        try
        {
            await WaitFor(worker, D215ConnectionStatus.Identified, stop.Token);
            gate.SetEnabled(D215ConnectionWorker.HandlerId, false);
            await WaitFor(worker, D215ConnectionStatus.CleanupFailed, stop.Token);
            gate.SetEnabled(D215ConnectionWorker.HandlerId, true);
            await Task.Delay(50, stop.Token);
            Assert.Equal(1, Volatile.Read(ref opens));
            Assert.Equal(1, wire.DisposeCount);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitFor(D215ConnectionWorker worker, D215ConnectionStatus status, CancellationToken ct)
    {
        while (worker.Snapshot.Status != status)
            await Task.Delay(5, ct);
    }

    private static ValueTask<D215Session> Connect(FakeWire wire, FakeDiscovery? discovery = null,
        TimeProvider? clock = null, CancellationToken cancellationToken = default) =>
        D215Session.ConnectAsync(Port, discovery ?? new FakeDiscovery { Ports = [Port] },
            (_, _) => ValueTask.FromResult<ID215SerialTransport>(wire), clock ?? TimeProvider.System, Timing, cancellationToken);

    private static byte[] InfoFrame(string version = "3.4", int brightness = 80)
    {
        var json = Encoding.UTF8.GetBytes($$"""{"cmd":"info","data":{"model":"D215-NOR-JD9365-6.67inch-hor","version":"{{version}}","brightness":{{brightness}},"diplay_on":false} }""");
        var frame = new byte[json.Length + 7];
        frame[0] = 0x55;
        frame[1] = 0xAA;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), (ushort)frame.Length);
        frame[4] = 0x06;
        json.CopyTo(frame, 5);
        int sum = 0;
        foreach (var value in frame.AsSpan(0, frame.Length - 2)) sum += value;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(frame.Length - 2), unchecked((ushort)sum));
        return frame;
    }

    private sealed class FakeDiscovery : ID215PortDiscovery
    {
        private IReadOnlyList<D215Port> _ports = [];
        internal IReadOnlyList<D215Port> Ports { get => Volatile.Read(ref _ports); set => Volatile.Write(ref _ports, value); }
        public IReadOnlyList<D215Port> Discover() => Ports;
    }

    private sealed class SingleFrame : ID215FrameSource
    {
        private byte[]? _envelope = Convert.FromHexString(JpegEnvelope);
        public bool MayRepeat => false;
        public ReadOnlyMemory<byte> TakeLatestEnvelope() => Interlocked.Exchange(ref _envelope, null);
    }

    private sealed class FakeWire(params byte[][] chunks) : ID215SerialTransport
    {
        private readonly Queue<byte[]> _chunks = new(chunks);
        internal List<string> Writes { get; } = [];
        internal int DisposeCount { get; private set; }
        internal bool FailDispose { get; init; }
        internal Task? HoldClose { get; init; }
        internal Task? HoldLiveFrame { get; init; }
        internal bool BlockFirstWrite { get; init; }
        internal bool ReadWasCancelled { get; private set; }
        internal TaskCompletionSource Closing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource WaitingForRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource WaitingForWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource WriteCancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
        {
            if (_chunks.TryDequeue(out var chunk))
            {
                chunk.AsSpan().CopyTo(destination.Span);
                return chunk.Length;
            }
            WaitingForRead.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { ReadWasCancelled = true; throw; }
            return 0;
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Writes.Add(Convert.ToHexString(source.Span));
            if (BlockFirstWrite && Writes.Count == 1)
            {
                WaitingForWrite.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            if (HoldLiveFrame is { } release && Writes[^1] == JpegEnvelope)
            {
                using var registration = ct.Register(() => WriteCancellationObserved.TrySetResult());
                WaitingForWrite.TrySetResult();
                await release;
            }
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            Closing.TrySetResult();
            // A native close can block before it returns an awaitable.
            HoldClose?.GetAwaiter().GetResult();
            if (FailDispose) return ValueTask.FromException(new IOException("Fake close failure"));
            Closed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly List<Timer> _timers = [];
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(callback, state, _ticks + dueTime.Ticks);
            _timers.Add(timer);
            return timer;
        }

        internal void Advance(TimeSpan elapsed)
        {
            _ticks += elapsed.Ticks;
            foreach (var timer in _timers.ToArray()) timer.FireIfDue(_ticks);
        }

        private sealed class Timer(TimerCallback callback, object? state, long due) : ITimer
        {
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            internal void FireIfDue(long ticks) { if (!_disposed && ticks >= due) callback(state); }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
