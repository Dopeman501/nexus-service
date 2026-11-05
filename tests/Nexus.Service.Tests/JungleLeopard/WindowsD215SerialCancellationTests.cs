#if WINDOWS
using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Nexus.Service.Devices;
using Nexus.Service.Peripherals.JungleLeopard;

namespace Nexus.Service.Tests.JungleLeopard;

public sealed class WindowsD215SerialCancellationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private const int PipeBufferBytes = 4 * 1024;
    private const string InfoReply = "55AA7700067B22636D64223A22696E666F222C2264617461223A7B226D6F6465"
        + "6C223A22443231352D4E4F522D4A44393336352D362E3637696E63682D686F72"
        + "222C2276657273696F6E223A22332E34222C226272696768746E657373223A38"
        + "302C226469706C61795F6F6E223A66616C73657D7D0D24";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Native_frame_interruption_releases_before_identifying_the_moved_device(bool peerCloses)
    {
        using var original = await PipePair.ConnectAsync();
        using var replacement = await PipePair.ConnectAsync();
        await using var firstTransport = new ObservedNativeWrite(BindTransport(original.Client));
        await using var nextTransport = BindTransport(replacement.Client);
        var firstPort = new D215Port("COM7", "private-pipe-test-device", true);
        var nextPort = firstPort with { PortName = "COM8" };
        var discovery = new PipePortDiscovery { Ports = [firstPort] };
        var gate = new DeviceControlGate(new InMemoryConfigStore());
        gate.SetEnabled(D215ConnectionWorker.HandlerId, true);
        var timing = D215Timing.Default with { PortSettle = TimeSpan.Zero, ClearSettle = TimeSpan.Zero, WriteTimeout = Deadline };
        var closedBeforeReplacement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int opens = 0;
        using var worker = new D215ConnectionWorker(discovery, gate, (port, _) =>
        {
            var count = Interlocked.Increment(ref opens);
            if (count == 1 && port == firstPort) return ValueTask.FromResult<ID215SerialTransport>(firstTransport);
            if (count != 2 || port != nextPort) throw new IOException("Unexpected port claim in the test.");
            closedBeforeReplacement.TrySetResult(original.Client.IsClosed);
            return ValueTask.FromResult<ID215SerialTransport>(nextTransport);
        }, TimeProvider.System, timing, D215ConnectionWorker.DefaultPollInterval);
        using var deadline = new CancellationTokenSource(Deadline);
        await worker.StartAsync(deadline.Token);
        try
        {
            await RespondToInfoAsync(original.Peer, deadline.Token);
            await WaitForStatusAsync(worker, D215ConnectionStatus.Identified, deadline.Token);
            var session = Assert.IsType<D215Session>(worker.GetIdentifiedSession(firstPort.Identity));
            var jpeg = new byte[256 * 1024];
            jpeg[0] = jpeg[^2] = 0xFF;
            jpeg[1] = 0xD8;
            jpeg[^1] = 0xD9;
            var envelope = D215Protocol.BuildJpegEnvelope(jpeg);
            var stream = session.BeginStreaming(new PipeFrameSource(envelope),
                () => ReferenceEquals(worker.GetIdentifiedSession(firstPort.Identity), session));
            await ExpectBytesAsync(original.Peer, "55AA0700111701", deadline.Token);
            await original.Peer.WriteAsync(Convert.FromHexString("55AA080011001801"), deadline.Token);
            await stream.Started.WaitAsync(deadline.Token);

            // Windows may buffer the entire frame without completing the overlapped write.
            var frameWrite = await firstTransport.FrameWrite.Task.WaitAsync(deadline.Token);
            await WaitForPipeBytesAsync(original.Peer, frameWrite);
            var prefix = new byte[sizeof(uint)];
            await original.Peer.ReadExactlyAsync(prefix, deadline.Token);
            Assert.Equal((uint)jpeg.Length, BinaryPrimitives.ReadUInt32LittleEndian(prefix));
            Assert.False(frameWrite.IsCompleted);
            if (peerCloses)
            {
                original.Peer.Dispose();
                await Assert.ThrowsAsync<IOException>(() => frameWrite.WaitAsync(deadline.Token));
                await WaitForStatusAsync(worker, D215ConnectionStatus.IoFailure, deadline.Token);
                Assert.IsType<IOException>(stream.Failure);
            }
            else
            {
                discovery.Ports = [];
                await WaitForStatusAsync(worker, D215ConnectionStatus.Absent, deadline.Token);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => frameWrite.WaitAsync(Deadline));
                Assert.Null(stream.Failure);
                using var pendingBytes = new MemoryStream();
                await original.Peer.CopyToAsync(pendingBytes, deadline.Token);
                var tail = pendingBytes.ToArray();
                Assert.InRange(tail.Length, 0, envelope.Length - prefix.Length);
                Assert.Equal(envelope.AsSpan(prefix.Length, tail.Length).ToArray(), tail);
            }
            Assert.Null(worker.Snapshot.Info);
            Assert.Null(worker.GetIdentifiedSession(firstPort.Identity));

            discovery.Ports = [nextPort];
            Assert.True(await closedBeforeReplacement.Task.WaitAsync(deadline.Token));
            await RespondToInfoAsync(replacement.Peer, deadline.Token);
            await WaitForStatusAsync(worker, D215ConnectionStatus.Identified, deadline.Token);
            Assert.Equal(nextPort, worker.Snapshot.Port);
            Assert.Equal("3.4", worker.Snapshot.Info?.Firmware);
            Assert.NotSame(session, worker.GetIdentifiedSession(firstPort.Identity));
            Assert.Equal(2, Volatile.Read(ref opens));
        }
        finally
        {
            original.Peer.Dispose();
            replacement.Peer.Dispose();
            await worker.StopAsync(CancellationToken.None).WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task A_queued_native_read_cancels_and_completes_before_disposal()
    {
        using var pipe = await PipePair.ConnectAsync();
        var binding = ThreadPoolBoundHandle.BindHandle(pipe.Client);
        using var cancellation = new CancellationTokenSource();
        IDisposable? transfer = null;
        Task? submittedCompletion = null;
        try
        {
            var transferType = typeof(WindowsD215SerialTransport).GetNestedType("PendingTransfer", BindingFlags.NonPublic)!;
            transfer = (IDisposable)Activator.CreateInstance(transferType,
                BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
                args: [pipe.Client, binding, new ReadOnlyMemory<byte>(new byte[1]), cancellation.Token], culture: null)!;
            var completion = (Task<int>)transferType.GetProperty("Completion", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(transfer)!;
            // Submit runs synchronously on the test thread. The connected peer never writes,
            // so this read is waiting in Windows before the token is canceled.
            transferType.GetMethod("Submit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(transfer, [false]);
            submittedCompletion = completion;
            Assert.False(completion.IsCompleted);
            await cancellation.CancelAsync().WaitAsync(Deadline);
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.WaitAsync(Deadline));
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        finally
        {
            pipe.Peer.Dispose();
            if (submittedCompletion is not null)
            {
                await Task.WhenAny(submittedCompletion, Task.Delay(Deadline));
                // Keep the pin and OVERLAPPED alive if the test exposes a missing completion.
                Assert.True(submittedCompletion.IsCompleted, "Native completion was still outstanding after the peer closed.");
            }
            try { transfer?.Dispose(); }
            finally { binding.Dispose(); }
        }
    }

    [Fact]
    public async Task Disposal_cancels_a_backpressured_write_and_the_peer_observes_close()
    {
        using var pipe = await PipePair.ConnectAsync();
        var transport = BindTransport(pipe.Client);
        var payload = new byte[256 * 1024];
        try
        {
            var write = transport.WriteAsync(payload, CancellationToken.None).AsTask();
            await WaitForPipeBytesAsync(pipe.Peer, write);
            Assert.False(write.IsCompleted);

            var firstClose = transport.DisposeAsync().AsTask();
            var secondClose = transport.DisposeAsync().AsTask();
            await Task.WhenAll(firstClose, secondClose).WaitAsync(Deadline);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.WaitAsync(Deadline));

            // Windows may have copied a prefix before cancellation. Drain it and prove the
            // peer sees kernel EOF, which requires closing the adapter's underlying handle.
            using var draining = new CancellationTokenSource(Deadline);
            var received = new byte[PipeBufferBytes];
            int total = 0;
            while (true)
            {
                int count = await pipe.Peer.ReadAsync(received, draining.Token).AsTask().WaitAsync(Deadline);
                if (count == 0) break;
                total += count;
                Assert.InRange(total, 1, payload.Length);
            }
        }
        finally
        {
            // If a regression leaves the write waiting, breaking the connection gives
            // the request an independent way to complete before adapter cleanup retries.
            pipe.Peer.Dispose();
            await transport.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    private static async Task WaitForPipeBytesAsync(NamedPipeServerStream peer, Task write)
    {
        using var waiting = new CancellationTokenSource(Deadline);
        while (true)
        {
            Assert.False(write.IsCompleted, "The fixture write completed before the peer applied backpressure.");
            if (!PeekNamedPipe(peer.SafePipeHandle, IntPtr.Zero, 0, IntPtr.Zero, out uint available, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (available > 0) return;
            await Task.Delay(TimeSpan.FromMilliseconds(10), waiting.Token);
        }
    }

    private static async Task RespondToInfoAsync(NamedPipeServerStream peer, CancellationToken token)
    {
        await ExpectBytesAsync(peer, "FFD9FFD90000000055AA0700060C01", token);
        await peer.WriteAsync(Convert.FromHexString(InfoReply), token);
    }

    private static async Task ExpectBytesAsync(NamedPipeServerStream peer, string hex, CancellationToken token)
    {
        var received = new byte[hex.Length / 2];
        await peer.ReadExactlyAsync(received, token);
        Assert.Equal(hex, Convert.ToHexString(received));
    }

    private static async Task WaitForStatusAsync(D215ConnectionWorker worker, D215ConnectionStatus status, CancellationToken token)
    {
        while (worker.Snapshot.Status != status)
            await Task.Delay(5, token);
    }

    private sealed class PipePortDiscovery : ID215PortDiscovery
    {
        private IReadOnlyList<D215Port> _ports = [];
        internal IReadOnlyList<D215Port> Ports { get => Volatile.Read(ref _ports); set => Volatile.Write(ref _ports, value); }
        public IReadOnlyList<D215Port> Discover() => Ports;
    }

    private sealed class PipeFrameSource(byte[] envelope) : ID215FrameSource
    {
        public ReadOnlyMemory<byte> TakeLatestEnvelope() => envelope;
        public bool MayRepeat => false;
    }

    private sealed class ObservedNativeWrite(WindowsD215SerialTransport transport) : ID215SerialTransport
    {
        internal TaskCompletionSource<Task> FrameWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token) => transport.ReadAsync(destination, token);

        public ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken token)
        {
            var pending = transport.WriteAsync(source, token);
            if (source.Length <= PipeBufferBytes) return pending;
            var write = pending.AsTask();
            FrameWrite.TrySetResult(write);
            return new ValueTask(write);
        }

        public ValueTask DisposeAsync() => transport.DisposeAsync();
    }

    private static WindowsD215SerialTransport BindTransport(SafeFileHandle client)
    {
        var binding = ThreadPoolBoundHandle.BindHandle(client);
        try
        {
            // Bypass only device discovery and COM configuration. The adapter still owns
            // real Win32 I/O, cancellation, pinning, serialization, and native close.
            return (WindowsD215SerialTransport)Activator.CreateInstance(typeof(WindowsD215SerialTransport),
                BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
                args: [client, binding], culture: null)!;
        }
        catch
        {
            binding.Dispose();
            throw;
        }
    }

    private sealed record PipePair(NamedPipeServerStream Peer, SafeFileHandle Client) : IDisposable
    {
        internal static async Task<PipePair> ConnectAsync()
        {
            var name = $"nexus-d215-test-{Guid.NewGuid():N}";
            var peer = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, PipeBufferBytes, PipeBufferBytes);
            SafeFileHandle? client = null;
            try
            {
                const uint GenericReadWrite = 0xC0000000;
                const uint OpenExisting = 3;
                const uint FileFlagOverlapped = 0x40000000;
                client = CreateFileW($@"\\.\pipe\{name}", GenericReadWrite, 0, IntPtr.Zero,
                    OpenExisting, FileFlagOverlapped, IntPtr.Zero);
                if (client.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
                using var connecting = new CancellationTokenSource(Deadline);
                await peer.WaitForConnectionAsync(connecting.Token).WaitAsync(Deadline);
                return new PipePair(peer, client);
            }
            catch
            {
                client?.Dispose();
                peer.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Peer.Dispose();
            Client.Dispose();
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint shareMode,
        IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(SafePipeHandle pipe, IntPtr buffer, uint bufferSize,
        IntPtr bytesRead, out uint totalBytesAvailable, IntPtr bytesLeftThisMessage);
}
#endif
