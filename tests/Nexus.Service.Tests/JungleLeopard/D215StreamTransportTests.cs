using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.JungleLeopard;

namespace Nexus.Service.Tests.JungleLeopard;

public sealed class D215StreamTransportTests
{
    private static readonly D215Port Port = new("COM7", "d215-test", true);
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(4);
    private static readonly D215Timing Timing = D215Timing.Default with
    {
        PortSettle = TimeSpan.Zero,
        ClearSettle = TimeSpan.Zero,
        ReplyTimeout = TimeSpan.FromMilliseconds(500),
    };

    [Fact]
    public void Rotation_uses_the_coordinators_existing_orientation_contract()
    {
        using var transport = CreateTransport(_ => null, () => new EchoEncoder());
        Assert.IsAssignableFrom<IOrientablePanelTransport>(transport);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Coordinator_orientation_is_applied_before_encoding_and_mirror_stays_ignored(bool flip180, bool mirror)
    {
        var wire = new FakeWire();
        await using var session = await IdentifyAsync(wire);
        using var transport = new D215StreamTransport(Port.Identity, _ => session,
            repeat => new D215BgraFrames(756 * 1680 * 4, _ => new EdgeEncoder(), repeat), TestDeadline);
        var orientable = Assert.IsAssignableFrom<IOrientablePanelTransport>(transport);
        orientable.BindOrientation(() => (flip180, mirror));
        transport.Open();
        var starting = Task.Run(transport.StartPlayer);
        await wire.LiveRequested.Task.WaitAsync(TestDeadline);
        wire.AcknowledgeLive();
        await starting.WaitAsync(TestDeadline);
        var frame = new byte[756 * 1680 * 4];
        new byte[] { 1, 2, 3, 4 }.CopyTo(frame, 0);
        new byte[] { 5, 6, 7, 8 }.CopyTo(frame, frame.Length - 4);

        transport.Write(frame);

        var envelope = await wire.FirstJpeg.Task.WaitAsync(TestDeadline);
        Assert.Equal(flip180 ? new byte[] { 5, 6, 7, 8, 1, 2, 3, 4 } : new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, envelope[4..^2]);
    }

    [Fact]
    public async Task Start_waits_for_the_ack_and_writes_only_complete_frames()
    {
        var wire = new FakeWire();
        await using var session = await IdentifyAsync(wire);
        using var transport = CreateTransport(_ => session, () => new EchoEncoder());
        transport.Open();

        var starting = Task.Run(transport.StartPlayer);
        await wire.LiveRequested.Task.WaitAsync(TestDeadline);
        Assert.False(starting.IsCompleted);
        Assert.Throws<IOException>(() => transport.Write([1, 2]));
        wire.AcknowledgeLive();
        await starting.WaitAsync(TestDeadline);

        transport.Write([1]);
        transport.Write([2, 3, 4]);
        Assert.Equal(new byte[] { 4, 0, 0, 0, 1, 2, 3, 4, 14, 0 },
            await wire.FirstJpeg.Task.WaitAsync(TestDeadline));

        transport.Dispose();
        Assert.False(transport.IsOpen);
        await wire.StreamCleared.Task.WaitAsync(TestDeadline);
    }

    [Fact]
    public async Task A_generation_lost_after_open_cannot_start_the_player()
    {
        var wire = new FakeWire();
        await using var session = await IdentifyAsync(wire);
        D215Session? current = session;
        using var transport = CreateTransport(_ => current, () => new EchoEncoder());
        transport.Open();
        current = null;

        Assert.Throws<IOException>(transport.StartPlayer);
        Assert.False(transport.IsOpen);
        Assert.False(wire.LiveRequested.Task.IsCompleted);
    }

    [Fact]
    public async Task Replacement_waits_for_the_old_stream_to_clear_before_entering_live_mode()
    {
        var wire = new FakeWire(holdStopClear: true);
        await using var session = await IdentifyAsync(wire);
        using var original = CreateTransport(_ => session, () => new EchoEncoder());
        original.Open();
        var originalStart = Task.Run(original.StartPlayer);
        await wire.LiveRequested.Task.WaitAsync(TestDeadline);
        wire.AcknowledgeLive();
        await originalStart.WaitAsync(TestDeadline);

        original.Dispose();
        using var replacement = CreateTransport(_ => session, () => new EchoEncoder());
        Task replacementStart;
        try
        {
            await wire.StreamClearRequested.Task.WaitAsync(TestDeadline);
            replacement.Open();
            replacementStart = Task.Run(replacement.StartPlayer);
            await Assert.ThrowsAsync<TimeoutException>(() => replacementStart.WaitAsync(TimeSpan.FromMilliseconds(100)));
            Assert.False(wire.NextLiveRequested.Task.IsCompleted);
        }
        finally
        {
            wire.FinishStreamClear.TrySetResult();
        }

        await wire.NextLiveRequested.Task.WaitAsync(TestDeadline);
        wire.AcknowledgeLive();
        await replacementStart.WaitAsync(TestDeadline);
        Assert.True(replacement.IsOpen);
    }

    [Fact]
    public async Task Disposing_during_encoding_defers_encoder_cleanup_until_the_pump_returns()
    {
        var wire = new FakeWire();
        await using var session = await IdentifyAsync(wire);
        var encoder = new BlockingEncoder();
        using var transport = CreateTransport(_ => session, () => encoder);
        transport.Open();
        var starting = Task.Run(transport.StartPlayer);
        await wire.LiveRequested.Task.WaitAsync(TestDeadline);
        wire.AcknowledgeLive();
        await starting.WaitAsync(TestDeadline);

        transport.Write([1, 2, 3, 4]);
        try
        {
            await encoder.Started.Task.WaitAsync(TestDeadline);
            await Task.Run(transport.Dispose).WaitAsync(TestDeadline);
            Assert.False(encoder.Disposed.Task.IsCompleted);
        }
        finally
        {
            encoder.Finish.TrySetResult();
        }
        await encoder.Disposed.Task.WaitAsync(TestDeadline);
        Assert.False(transport.IsOpen);
    }

    [Fact]
    public async Task An_unacknowledged_start_expires_and_retires_the_transport()
    {
        var wire = new FakeWire();
        await using var session = await IdentifyAsync(wire);
        using var transport = CreateTransport(_ => session, () => new EchoEncoder(), TimeSpan.FromMilliseconds(50));
        transport.Open();

        await Assert.ThrowsAsync<TimeoutException>(() => Task.Run(transport.StartPlayer));
        Assert.False(transport.IsOpen);
        Assert.False(session.CanStream);
        Assert.Throws<ObjectDisposedException>(() => transport.Write([1, 2, 3, 4]));
    }

    private static D215StreamTransport CreateTransport(Func<string, D215Session?> lookup,
        Func<ID215JpegEncoder> encoder, TimeSpan? startTimeout = null)
        => new(Port.Identity, lookup, repeat => new D215BgraFrames(4, _ => encoder(), repeat),
            startTimeout ?? TimeSpan.FromSeconds(3));

    private static ValueTask<D215Session> IdentifyAsync(FakeWire wire) =>
        D215Session.ConnectAsync(Port, new FakeDiscovery(), (_, _) => ValueTask.FromResult<ID215SerialTransport>(wire),
            TimeProvider.System, Timing, CancellationToken.None);

    private sealed class FakeDiscovery : ID215PortDiscovery
    {
        public IReadOnlyList<D215Port> Discover() => [Port];
    }

    private sealed class EchoEncoder : ID215JpegEncoder
    {
        public ReadOnlySpan<byte> Encode(ReadOnlySpan<byte> frame) => frame;
        public void Dispose() { }
    }

    private sealed class EdgeEncoder : ID215JpegEncoder
    {
        private readonly byte[] _edges = new byte[8];
        public ReadOnlySpan<byte> Encode(ReadOnlySpan<byte> frame)
        {
            frame[..4].CopyTo(_edges);
            frame[^4..].CopyTo(_edges.AsSpan(4));
            return _edges;
        }
        public void Dispose() { }
    }

    private sealed class BlockingEncoder : ID215JpegEncoder
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ReadOnlySpan<byte> Encode(ReadOnlySpan<byte> frame)
        {
            Started.TrySetResult();
            Finish.Task.Wait(TimeSpan.FromSeconds(8));
            return frame;
        }
        public void Dispose() => Disposed.TrySetResult();
    }

    private sealed class FakeWire(bool holdStopClear = false) : ID215SerialTransport
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private int _clearCount;
        internal TaskCompletionSource LiveRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource NextLiveRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<byte[]> FirstJpeg { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource StreamClearRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FinishStreamClear { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource StreamCleared { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void AcknowledgeLive() => _replies.Writer.TryWrite(Convert.FromHexString("55AA080011001801"));

        public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            var next = await _replies.Reader.ReadAsync(cancellationToken);
            next.AsSpan().CopyTo(destination.Span);
            return next.Length;
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (Convert.ToHexString(source.Span))
            {
                case "55AA0700060C01":
                    _replies.Writer.TryWrite(InfoReply());
                    break;
                case "55AA0700111701":
                    if (!LiveRequested.TrySetResult()) NextLiveRequested.TrySetResult();
                    break;
                case "00000000":
                    if (Interlocked.Increment(ref _clearCount) > 1)
                    {
                        StreamClearRequested.TrySetResult();
                        if (holdStopClear) await FinishStreamClear.Task.WaitAsync(cancellationToken);
                        StreamCleared.TrySetResult();
                    }
                    break;
                case "FFD9FFD9":
                    break;
                default:
                    FirstJpeg.TrySetResult(source.ToArray());
                    break;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static byte[] InfoReply()
        {
            ReadOnlySpan<byte> payload = """{"cmd":"info","data":{"model":"D215-NOR-JD9365-6.67inch-hor","version":"3.4","brightness":80,"diplay_on":false}}"""u8;
            var frame = new byte[payload.Length + 7];
            frame[0] = 0x55;
            frame[1] = 0xAA;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), (ushort)frame.Length);
            frame[4] = 0x06;
            payload.CopyTo(frame.AsSpan(5));
            int sum = 0;
            foreach (var value in frame.AsSpan(0, frame.Length - 2)) sum += value;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(frame.Length - 2), unchecked((ushort)sum));
            return frame;
        }
    }
}
