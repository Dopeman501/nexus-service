using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Text;
using System.Threading.Channels;
using Nexus.Service.Devices;
using Nexus.Service.Peripherals.JungleLeopard;

namespace Nexus.Service.Tests.JungleLeopard;

public sealed class D215RestartTests
{
    private const string Restart = "55AA0700010701";
    private static readonly D215Port Port = new("COM7", "verified-d215", true);
    private static readonly D215Timing Timing = D215Timing.Default with
    {
        PortSettle = TimeSpan.Zero, ClearSettle = TimeSpan.Zero,
        ReplyTimeout = TimeSpan.FromMilliseconds(100), ReleaseTimeout = TimeSpan.FromMilliseconds(200),
        RestartTimeout = TimeSpan.FromSeconds(2),
    };

    [Fact]
    public void Restart_packet_matches_verified_device_command() =>
        Assert.Equal(Restart, Convert.ToHexString(D215Protocol.BuildRestartRequest()));

    [Fact]
    public async Task Restart_waits_for_the_old_lease_then_reidentifies_and_waits_for_live_ack()
    {
        await using var harness = await Harness.Start();
        var old = harness.Wires.First();
        var result = harness.Worker.RestartAsync(Port.Identity, () => true, CancellationToken.None).AsTask();
        await harness.RestartWritten.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await harness.Discovery.GoneObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, old.DisposeCount);
        Assert.Equal("busy", (await harness.Worker.RestartAsync(Port.Identity, () => true, CancellationToken.None)).Status);
        harness.Discovery.Ports = [Port];
        await harness.WaitIdentified();
        Assert.False(result.IsCompleted);
        var recovered = Assert.IsType<D215Session>(harness.Worker.GetIdentifiedSession(Port.Identity));
        var stream = recovered.BeginStreaming(new NoFrames(), () => true);
        await stream.Started.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("restarted", (await result.WaitAsync(TimeSpan.FromSeconds(3))).Status);
        Assert.Equal(1, harness.Wires.Sum(wire => wire.Writes.Count(packet => packet == Restart)));
        Assert.Equal(3, harness.Wires.Count);
        Assert.NotSame(harness.OriginalSession, recovered);
        Assert.Equal(100, recovered.ConfirmedBrightness);
        Assert.Contains("55AA080003646E01", harness.Wires.Last().Writes);
    }

    [Fact]
    public async Task Restart_can_recover_after_the_old_stream_faults_without_a_healthy_session()
    {
        await using var harness = await Harness.Start(failNormalReopens: true);
        var oldStream = harness.OriginalSession.BeginStreaming(new NoFrames(), () => true);
        await oldStream.Started.WaitAsync(TimeSpan.FromSeconds(2));
        oldStream.Fail(new IOException("simulated stalled player"));
        await harness.WaitUntil(() => harness.Worker.Snapshot.Status == D215ConnectionStatus.NoResponse && harness.Wires.Count >= 2);
        var result = harness.Worker.RestartAsync(Port.Identity, () => true, CancellationToken.None).AsTask();
        await harness.RestartWritten.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await harness.Discovery.GoneObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        harness.Discovery.Ports = [Port];
        await harness.WaitIdentified();
        var session = Assert.IsType<D215Session>(harness.Worker.GetIdentifiedSession(Port.Identity));
        await session.BeginStreaming(new NoFrames(), () => true).Started.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("restarted", (await result).Status);
        Assert.Equal(1, harness.Wires.Sum(wire => wire.Writes.Count(packet => packet == Restart)));
    }

    [Fact]
    public async Task An_unconfirmed_reboot_is_not_retransmitted_and_reports_uncertain()
    {
        await using var harness = await Harness.Start(reenumerate: false);
        var result = await harness.Worker.RestartAsync(Port.Identity, () => true, CancellationToken.None);
        Assert.Equal("uncertain", result.Status);
        Assert.Equal(1, harness.Wires.Sum(wire => wire.Writes.Count(packet => packet == Restart)));
    }

    [Fact]
    public async Task Wrong_identity_disabled_gate_and_revoked_record_never_restart_the_wire()
    {
        await using var harness = await Harness.Start();
        Assert.Equal("unavailable", (await harness.Worker.RestartAsync("different-device", () => true, CancellationToken.None)).Status);
        Assert.Equal("unavailable", (await harness.Worker.RestartAsync(Port.Identity, () => false, CancellationToken.None)).Status);
        harness.Gate.SetEnabled(D215ConnectionWorker.HandlerId, false);
        Assert.Equal("unavailable", (await harness.Worker.RestartAsync(Port.Identity, () => true, CancellationToken.None)).Status);
        Assert.DoesNotContain(harness.Wires.SelectMany(wire => wire.Writes), packet => packet == Restart);
    }

    [Fact]
    public async Task A_held_old_lease_times_out_without_opening_a_second_port_or_sending_restart()
    {
        await using var harness = await Harness.Start();
        var old = harness.Wires.First();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        old.HoldClose = release.Task;
        try
        {
            var result = await harness.Worker.RestartAsync(Port.Identity, () => true, CancellationToken.None);
            Assert.Equal("failed", result.Status);
            Assert.Single(harness.Wires);
            Assert.Equal("busy", (await harness.Worker.RestartAsync(Port.Identity, () => true, CancellationToken.None)).Status);
            Assert.DoesNotContain(old.Writes, packet => packet == Restart);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Expiry_while_info_is_held_suppresses_a_late_restart_dispatch()
    {
        var releaseInfo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await Harness.Start(holdRestartInfo: releaseInfo.Task);
        var pending = harness.Worker.RestartAsync(Port.Identity, () => true, CancellationToken.None).AsTask();
        await harness.WaitUntil(() => harness.Wires.Count == 2);
        var rebootWire = harness.Wires.Last();
        await rebootWire.ReadHeld.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("failed", (await pending.WaitAsync(TimeSpan.FromSeconds(3))).Status);
        releaseInfo.TrySetResult();
        await rebootWire.Closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(rebootWire.Writes, packet => packet == Restart);
        Assert.False(harness.RestartWritten.Task.IsCompleted);
    }

    [Fact]
    public async Task Revoking_the_record_while_the_old_lease_closes_prevents_restart()
    {
        await using var harness = await Harness.Start();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = harness.Wires.First();
        old.HoldClose = release.Task;
        var authorized = 1;
        var pending = harness.Worker.RestartAsync(Port.Identity, () => Volatile.Read(ref authorized) == 1,
            CancellationToken.None).AsTask();
        await harness.WaitUntil(() => old.DisposeCount == 1);
        Volatile.Write(ref authorized, 0);
        release.TrySetResult();
        Assert.Equal("unavailable", (await pending).Status);
        Assert.DoesNotContain(harness.Wires.SelectMany(wire => wire.Writes), packet => packet == Restart);
    }

    [Fact]
    public async Task Brightness_mismatch_after_reboot_never_reports_restarted()
    {
        await using var harness = await Harness.Start(ignoreRestore: true);
        var result = harness.Worker.RestartAsync(Port.Identity, () => true, CancellationToken.None).AsTask();
        await harness.RestartWritten.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await harness.Discovery.GoneObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        harness.Discovery.Ports = [Port];
        await harness.WaitIdentified();
        var recovered = Assert.IsType<D215Session>(harness.Worker.GetIdentifiedSession(Port.Identity));
        await recovered.BeginStreaming(new NoFrames(), () => true).Started.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("uncertain", (await result).Status);
        Assert.Equal(80, recovered.ConfirmedBrightness);
        Assert.Equal(1, harness.Wires.Sum(wire => wire.Writes.Count(packet => packet == Restart)));
    }

    [Fact]
    public async Task A_failed_restart_port_close_quarantines_the_lease_without_reopening()
    {
        await using var harness = await Harness.Start(failRestartClose: true);
        Assert.Equal("uncertain", (await harness.Worker.RestartAsync(Port.Identity, () => true, CancellationToken.None)).Status);
        await harness.WaitUntil(() => harness.Worker.Snapshot.Status == D215ConnectionStatus.CleanupFailed);
        Assert.Equal(2, harness.Wires.Count);
        Assert.Equal("unavailable", (await harness.Worker.RestartAsync(Port.Identity, () => true, CancellationToken.None)).Status);
        Assert.Equal(2, harness.Wires.Count);
    }

    [Fact]
    public async Task Revocation_after_the_reboot_write_reports_uncertain_without_retransmission()
    {
        await using var harness = await Harness.Start();
        var authorized = 1;
        var result = harness.Worker.RestartAsync(Port.Identity, () => Volatile.Read(ref authorized) == 1, CancellationToken.None).AsTask();
        await harness.RestartWritten.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Volatile.Write(ref authorized, 0);
        Assert.Equal("uncertain", (await result).Status);
        Assert.Equal(1, harness.Wires.Sum(wire => wire.Writes.Count(packet => packet == Restart)));
    }

    private sealed class NoFrames : ID215FrameSource
    {
        public bool MayRepeat => false;
        public ReadOnlyMemory<byte> TakeLatestEnvelope() => default;
    }

    private sealed class Harness : IAsyncDisposable
    {
        internal Discovery Discovery { get; } = new();
        internal DeviceControlGate Gate { get; } = new(new InMemoryConfigStore());
        internal ConcurrentQueue<Wire> Wires { get; } = new();
        internal TaskCompletionSource RestartWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal D215ConnectionWorker Worker { get; private set; } = null!;
        internal D215Session OriginalSession { get; private set; } = null!;

        internal static async Task<Harness> Start(bool reenumerate = true, bool failNormalReopens = false,
            Task? holdRestartInfo = null, bool ignoreRestore = false, bool failRestartClose = false)
        {
            var harness = new Harness();
            harness.Gate.SetEnabled(D215ConnectionWorker.HandlerId, true);
            var generation = 0;
            harness.Worker = new D215ConnectionWorker(harness.Discovery, harness.Gate, (_, _) =>
            {
                var index = Interlocked.Increment(ref generation);
                var wire = new Wire
                {
                    Brightness = harness.RestartWritten.Task.IsCompleted ? 80 : 100,
                    HoldRead = index == 2 ? holdRestartInfo : null,
                    IgnoreBrightness = ignoreRestore,
                    FailClose = index == 2 && failRestartClose,
                    ReplyToInfo = !failNormalReopens || index == 1 || harness.RestartWritten.Task.IsCompleted,
                    OnRestart = () =>
                    {
                        if (reenumerate) harness.Discovery.Ports = [];
                        harness.RestartWritten.TrySetResult();
                    },
                };
                harness.Wires.Enqueue(wire);
                return ValueTask.FromResult<ID215SerialTransport>(wire);
            }, TimeProvider.System, Timing, TimeSpan.FromMilliseconds(10));
            await harness.Worker.StartAsync(CancellationToken.None);
            await harness.WaitIdentified();
            harness.OriginalSession = Assert.IsType<D215Session>(harness.Worker.GetIdentifiedSession(Port.Identity));
            return harness;
        }

        internal Task WaitIdentified() => WaitUntil(() => Worker.GetIdentifiedSession(Port.Identity) is not null);
        internal async Task WaitUntil(Func<bool> condition)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!condition()) await Task.Delay(5, deadline.Token);
        }
        public async ValueTask DisposeAsync()
        {
            await Worker.StopAsync(CancellationToken.None);
            Worker.Dispose();
        }
    }

    private sealed class Discovery : ID215PortDiscovery
    {
        private IReadOnlyList<D215Port> _ports = [Port];
        internal TaskCompletionSource GoneObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IReadOnlyList<D215Port> Ports { get => Volatile.Read(ref _ports); set => Volatile.Write(ref _ports, value); }
        public IReadOnlyList<D215Port> Discover()
        {
            var current = Ports;
            if (current.Count == 0) GoneObserved.TrySetResult();
            return current;
        }
    }

    private sealed class Wire : ID215SerialTransport
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        internal ConcurrentQueue<string> Writes { get; } = new();
        internal bool ReplyToInfo { get; init; } = true;
        internal Action? OnRestart { get; init; }
        internal int Brightness { get; set; } = 100;
        internal Task? HoldClose { get; set; }
        internal Task? HoldRead { get; init; }
        internal bool IgnoreBrightness { get; init; }
        internal bool FailClose { get; init; }
        internal TaskCompletionSource ReadHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DisposeCount { get; private set; }
        public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
        {
            if (HoldRead is { } held) { ReadHeld.TrySetResult(); await held; }
            var reply = await _replies.Reader.ReadAsync(ct);
            reply.AsSpan().CopyTo(destination.Span);
            return reply.Length;
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var packet = Convert.ToHexString(source.Span);
            Writes.Enqueue(packet);
            if (packet == "55AA0700060C01" && ReplyToInfo)
            {
                var json = Encoding.UTF8.GetBytes("{\"cmd\":\"info\",\"data\":{\"model\":\"D215-NOR-JD9365-6.67inch-hor\",\"version\":\"3.4\",\"brightness\":" + Brightness + ",\"diplay_on\":false}}");
                var frame = new byte[json.Length + 7];
                frame[0] = 0x55;
                frame[1] = 0xAA;
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), (ushort)frame.Length);
                frame[4] = 0x06;
                json.CopyTo(frame, 5);
                ushort sum = 0;
                foreach (var value in frame.AsSpan(0, frame.Length - 2)) sum += value;
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(frame.Length - 2), sum);
                _replies.Writer.TryWrite(frame);
            }
            if (packet == "55AA0700111701") _replies.Writer.TryWrite(Convert.FromHexString("55AA080011001801"));
            if (source.Length == 8 && source.Span[4] == 0x03)
            {
                if (!IgnoreBrightness) Brightness = source.Span[5];
                _replies.Writer.TryWrite(Convert.FromHexString("55AA080003000A01"));
            }
            if (packet == Restart) OnRestart?.Invoke();
            return ValueTask.CompletedTask;
        }
        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (HoldClose is { } close) await close;
            if (FailClose) throw new IOException("Simulated driver close failure");
            Closed.TrySetResult();
        }
    }
}
