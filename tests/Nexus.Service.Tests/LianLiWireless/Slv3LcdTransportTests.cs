#if WINDOWS
using System.Runtime.InteropServices;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit.Abstractions;

namespace Nexus.Service.Tests.LianLiWireless;

public sealed class Slv3LcdTransportTests(ITestOutputHelper output)
{
    [Fact]
    public void Warmed_image_push_reuses_one_exact_packet_without_payload_sized_allocations()
    {
        using var native = new FakeSlv3LcdWriteIo();
        using var transport = new Slv3LcdTransport(native);
        byte[] jpeg = [0xFF, 0xD8, 1, 2, 0xFF, 0xD9];
        for (int i = 0; i < 32; i++) Assert.True(transport.PushImage(jpeg));
        const int count = 2048;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < count; i++)
            if (!transport.PushImage(jpeg)) throw new InvalidOperationException("Fake complete write failed.");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"Warmed pushes={count};allocated={allocated};bytes/push={(double)allocated / count};samePacket={native.SamePacket}");
        Assert.True(allocated < (long)count * 16_384, $"Warmed pushes allocated {allocated} bytes; payload-sized per-frame allocation remains.");
        Assert.True(native.SamePacket);
        Assert.Equal(102400, native.FirstPacket!.Length);
        Assert.Equal(0, native.ActivePins);
        Assert.Equal(0, native.ActiveStorage);
    }

    [Theory]
    [InlineData((int)FakeSlv3LcdWriteIo.Failure.Pin)]
    [InlineData((int)FakeSlv3LcdWriteIo.Failure.Allocate)]
    [InlineData((int)FakeSlv3LcdWriteIo.Failure.Initialize)]
    [InlineData((int)FakeSlv3LcdWriteIo.Failure.Write)]
    public void Partial_write_setup_failure_releases_only_acquired_resources(int failure)
    {
        using var native = new FakeSlv3LcdWriteIo { FailAt = (FakeSlv3LcdWriteIo.Failure)failure };
        using var transport = new Slv3LcdTransport(native);
        Assert.Throws<InvalidOperationException>(() => transport.PushImage([1, 2, 3]));
        Assert.Equal(0, native.ActivePins);
        Assert.Equal(0, native.ActiveStorage);
        Assert.Equal(native.PinsAcquired, native.PinsFreed);
        Assert.Equal(native.StorageAcquired, native.StorageFreed);
    }

    [Fact]
    public void Immediate_short_or_failed_write_returns_false_without_pending_reap()
    {
        using var native = new FakeSlv3LcdWriteIo { TransferredOverride = 12 };
        using var transport = new Slv3LcdTransport(native);
        Assert.False(transport.PushImage([1]));
        native.TransferredOverride = null;
        native.ImmediateFailure = true;
        Assert.False(transport.PushImage([2]));
        Assert.Equal(0, native.WaitCalls);
        Assert.Equal(0, native.ReapCalls);
        Assert.Equal(0, native.ActivePins);
        Assert.Equal(0, native.ActiveStorage);
    }

    [Theory]
    [InlineData(0u, true, false)]
    [InlineData(258u, true, true)]
    [InlineData(uint.MaxValue, false, true)]
    public void Pending_write_reaps_terminal_result_after_wait_and_optional_abort(uint waitResult, bool success, bool abort)
    {
        using var native = new FakeSlv3LcdWriteIo { Pending = true, WaitResult = waitResult, ReapSuccess = success };
        using var transport = new Slv3LcdTransport(native);
        Assert.Equal(success, transport.PushImage([1, 2]));
        Assert.Equal(1, native.WaitCalls);
        Assert.Equal(1, native.ReapCalls);
        Assert.Equal(abort ? 1 : 0, native.AbortCalls);
        Assert.Equal(0, native.ActivePins);
        Assert.Equal(0, native.ActiveStorage);
    }

    [Fact]
    public void Zero_storage_result_unpins_without_releasing_unowned_storage()
    {
        using var native = new FakeSlv3LcdWriteIo { ReturnZeroStorage = true };
        using var transport = new Slv3LcdTransport(native);
        Assert.Throws<OutOfMemoryException>(() => transport.PushImage([1]));
        Assert.Equal(0, native.ActivePins);
        Assert.Equal(0, native.StorageAcquired);
        Assert.Equal(0, native.StorageFreed);
    }

    [Fact]
    public void Pin_is_released_even_when_storage_cleanup_throws()
    {
        using var native = new FakeSlv3LcdWriteIo { ThrowAfterStorageFree = true };
        using var transport = new Slv3LcdTransport(native);
        Assert.Throws<InvalidOperationException>(() => transport.PushImage([1]));
        Assert.Equal(0, native.ActivePins);
        Assert.Equal(0, native.ActiveStorage);
    }

    [Fact]
    public void Independent_transports_own_different_exact_packets()
    {
        using var a = new FakeSlv3LcdWriteIo();
        using var b = new FakeSlv3LcdWriteIo();
        using var first = new Slv3LcdTransport(a);
        using var second = new Slv3LcdTransport(b);
        Assert.True(first.PushImage([1]));
        Assert.True(second.PushImage([2]));
        Assert.NotSame(a.FirstPacket, b.FirstPacket);
        Assert.Equal(102400, a.FirstPacket!.Length);
        Assert.Equal(102400, b.FirstPacket!.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(101888)]
    public void Actual_push_writes_exact_header_payload_padding_and_header_only_controls(int jpegLength)
    {
        using var native = new FakeSlv3LcdWriteIo { CaptureWrites = true };
        using var transport = new Slv3LcdTransport(native);
        var jpeg = new byte[jpegLength];
        Array.Fill(jpeg, (byte)0xA7);
        Assert.True(transport.PushImage(jpeg));
        Assert.True(transport.SetBrightness(42));
        Assert.True(transport.SetRotation(3));
        var packet = native.Writes[0];
        Assert.Equal(102400, packet.Length);
        Assert.Equal(jpeg, packet.AsSpan(512, jpegLength).ToArray());
        Assert.True(packet.AsSpan(512 + jpegLength).IndexOfAnyExcept((byte)0) < 0);
        var header = DecryptHeader(packet);
        Assert.Equal(101, header[0]);
        Assert.Equal(0x1A, header[2]);
        Assert.Equal(0x6D, header[3]);
        Assert.Equal((uint)jpegLength, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)));
        Assert.Equal(512, native.Writes[1].Length);
        Assert.Equal(512, native.Writes[2].Length);
        var brightness = DecryptHeader(native.Writes[1]);
        var rotation = DecryptHeader(native.Writes[2]);
        Assert.Equal(14, brightness[0]);
        Assert.Equal(42, brightness[8]);
        Assert.Equal(13, rotation[0]);
        Assert.Equal(3, rotation[8]);
    }

    [Fact]
    public void Invalid_payload_does_not_start_io_and_null_fake_boundary_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new Slv3LcdTransport((ISlv3LcdWriteIo)null!));
        using var native = new FakeSlv3LcdWriteIo();
        using var transport = new Slv3LcdTransport(native);
        Assert.Throws<ArgumentException>(() => transport.PushImage(new byte[101889]));
        Assert.Equal(0, native.WriteCalls);
        Assert.Equal(0, native.PinsAcquired);
        Assert.True(transport.PushImage([1]));
    }

    private static byte[] DecryptHeader(byte[] bytes)
    {
        using var des = System.Security.Cryptography.DES.Create();
        des.Key = Slv3LcdProtocol.DesKeyIv;
        return des.DecryptCbc(bytes.AsSpan(0, 512), Slv3LcdProtocol.DesKeyIv, System.Security.Cryptography.PaddingMode.PKCS7);
    }

    [Fact]
    public void Pending_short_completion_reports_failure_after_reaping()
    {
        using var native = new FakeSlv3LcdWriteIo { Pending = true, TransferredOverride = 2 };
        using var transport = new Slv3LcdTransport(native);
        Assert.False(transport.PushImage([1]));
        Assert.Equal(1, native.ReapCalls);
        Assert.False(native.NativeInFlight);
        Assert.Equal(0, native.ActivePins);
    }

    [Theory]
    [InlineData((int)FakeSlv3LcdWriteIo.Failure.Wait)]
    [InlineData((int)FakeSlv3LcdWriteIo.Failure.Abort)]
    public void Pending_wait_or_abort_exception_still_reaps_before_releasing_resources(int failure)
    {
        using var native = new FakeSlv3LcdWriteIo { Pending = true, WaitResult = 258, FailAt = (FakeSlv3LcdWriteIo.Failure)failure };
        using var transport = new Slv3LcdTransport(native);
        Assert.Throws<InvalidOperationException>(() => transport.PushImage([1]));
        Assert.Equal(1, native.ReapCalls);
        Assert.False(native.NativeInFlight);
        Assert.Equal(0, native.ActivePins);
        Assert.Equal(0, native.ActiveStorage);
    }

    [Fact]
    public async Task Image_and_control_wait_without_mutating_an_aborted_but_unreaped_packet()
    {
        using var native = new FakeSlv3LcdWriteIo { Pending = true, WaitResult = 258, ParkFirstReap = true, CaptureWrites = true };
        using var transport = new Slv3LcdTransport(native);
        var jpeg = new byte[90000];
        Array.Fill(jpeg, (byte)0xDD);
        var first = Task.Run(() => transport.PushImage(jpeg));
        Task<bool>? image = null, control = null;
        try
        {
            Assert.True(native.ReapEntered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, native.AbortCalls);
            var held = native.CurrentBuffer!.ToArray();
            using var attempted = new CountdownEvent(2);
            image = Task.Run(() => { attempted.Signal(); return transport.PushImage([1, 2]); });
            control = Task.Run(() => { attempted.Signal(); return transport.SetRotation(3); });
            Assert.True(attempted.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, native.WriteCalls);
            Assert.True(native.NativeInFlight);
            Assert.Equal(1, native.ActivePins);
            Assert.Equal(1, native.ActiveStorage);
            Assert.Equal(held, native.CurrentBuffer);
            native.ReleaseReap.Set();
            Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(await image.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(await control.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(native.SamePacket);
            Assert.Equal(3, native.WriteCalls);
            var small = Assert.Single(native.Writes, bytes => bytes.Length == 102400 && bytes[512] == 1);
            Assert.Equal(2, small[513]);
            Assert.True(small.AsSpan(514).IndexOfAnyExcept((byte)0) < 0);
            Assert.Single(native.Writes, bytes => bytes.Length == 512);
            Assert.Equal(0, native.ActivePins);
        }
        finally
        {
            native.ReleaseReap.Set();
            await Task.WhenAll(new[] { first, image, control }.OfType<Task>()).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Disposal_waits_for_terminal_native_completion_then_remains_idempotent()
    {
        using var native = new FakeSlv3LcdWriteIo { Pending = true, ParkFirstReap = true };
        var transport = new Slv3LcdTransport(native);
        var writing = Task.Run(() => transport.PushImage([1, 2, 3]));
        Task? disposing = null;
        try
        {
            Assert.True(native.ReapEntered.Wait(TimeSpan.FromSeconds(5)));
            disposing = Task.Run(transport.Dispose);
            Assert.True(SpinWait.SpinUntil(() => !transport.IsOpen, TimeSpan.FromSeconds(5)));
            Assert.False(disposing.IsCompleted);
            Assert.True(native.NativeInFlight);
            Assert.Equal(1, native.ActivePins);
            Assert.Equal(1, native.ActiveStorage);
            native.ReleaseReap.Set();
            Assert.True(await writing.WaitAsync(TimeSpan.FromSeconds(5)));
            await disposing.WaitAsync(TimeSpan.FromSeconds(5));
            transport.Dispose();
            Assert.False(transport.PushImage([9]));
            Assert.False(transport.SetBrightness(50));
            Assert.False(transport.SetRotation(2));
            Assert.Equal(1, native.WriteCalls);
            Assert.Equal(1, native.PinsFreed);
            Assert.Equal(1, native.StorageFreed);
        }
        finally
        {
            native.ReleaseReap.Set();
            await writing.WaitAsync(TimeSpan.FromSeconds(5));
            if (disposing is not null) await disposing.WaitAsync(TimeSpan.FromSeconds(5));
            transport.Dispose();
        }
    }
}

internal sealed class FakeSlv3LcdWriteIo : ISlv3LcdWriteIo, IDisposable
{
    public enum Failure { None, Pin, Allocate, Initialize, Write, Wait, Abort }
    private readonly Dictionary<nint, GCHandle> _pins = new();
    private readonly HashSet<nint> _storage = new();
    private int _length;
    public Failure FailAt { get; set; }
    public bool Pending { get; set; }
    public bool ImmediateFailure { get; set; }
    public bool ReapSuccess { get; set; } = true;
    public uint WaitResult { get; set; }
    public uint? TransferredOverride { get; set; }
    public byte[]? FirstPacket { get; private set; }
    public byte[]? CurrentBuffer { get; private set; }
    public bool SamePacket { get; private set; } = true;
    public bool ReturnZeroStorage { get; set; }
    public bool ThrowAfterStorageFree { get; set; }
    public bool ParkFirstReap { get; set; }
    public bool CaptureWrites { get; set; }
    public bool NativeInFlight { get; private set; }
    public ManualResetEventSlim ReapEntered { get; } = new();
    public ManualResetEventSlim ReleaseReap { get; } = new();
    public List<byte[]> Writes { get; } = new();
    public int ActivePins => _pins.Count;
    public int ActiveStorage => _storage.Count;
    public int PinsAcquired { get; private set; }
    public int PinsFreed { get; private set; }
    public int StorageAcquired { get; private set; }
    public int StorageFreed { get; private set; }
    public int WaitCalls { get; private set; }
    public int ReapCalls { get; private set; }
    public int AbortCalls { get; private set; }
    public int WriteCalls { get; private set; }

    private void ThrowAt(Failure step)
    {
        if (FailAt == step) throw new InvalidOperationException($"Injected {step} failure.");
    }
    public GCHandle Pin(byte[] buffer)
    {
        ThrowAt(Failure.Pin);
        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        _pins.Add(GCHandle.ToIntPtr(pin), pin);
        PinsAcquired++;
        CurrentBuffer = buffer;
        return pin;
    }
    public IntPtr AllocateOverlapped()
    {
        ThrowAt(Failure.Allocate);
        if (ReturnZeroStorage) return IntPtr.Zero;
        var storage = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        _storage.Add(storage);
        StorageAcquired++;
        return storage;
    }
    public void InitializeOverlapped(IntPtr storage)
    {
        ThrowAt(Failure.Initialize);
        Assert.Contains(storage, _storage);
    }
    public bool Write(GCHandle pin, int length, IntPtr storage, out uint transferred, out int error)
    {
        ThrowAt(Failure.Write);
        var buffer = Assert.IsType<byte[]>(pin.Target);
        Assert.Equal(buffer.Length, length);
        Assert.Contains(storage, _storage);
        _length = length;
        WriteCalls++;
        if (CaptureWrites) Writes.Add(buffer.ToArray());
        if (length == Slv3LcdProtocol.TransferBufferSize)
        {
            FirstPacket ??= buffer;
            SamePacket &= ReferenceEquals(FirstPacket, buffer);
        }
        transferred = TransferredOverride ?? (uint)length;
        error = Pending ? 997 : ImmediateFailure ? 31 : 0;
        NativeInFlight = Pending;
        return !Pending && !ImmediateFailure;
    }
    public uint Wait(uint milliseconds)
    {
        Assert.Equal(3000u, milliseconds);
        WaitCalls++;
        ThrowAt(Failure.Wait);
        return WaitResult;
    }
    public void Abort()
    {
        AbortCalls++;
        ThrowAt(Failure.Abort);
    }
    public bool Reap(IntPtr storage, out uint transferred)
    {
        Assert.Contains(storage, _storage);
        ReapCalls++;
        if (ParkFirstReap)
        {
            ParkFirstReap = false;
            ReapEntered.Set();
            if (!ReleaseReap.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Private fake reap was not released.");
        }
        NativeInFlight = false;
        transferred = TransferredOverride ?? (uint)_length;
        return ReapSuccess;
    }
    public void FreeOverlapped(IntPtr storage)
    {
        Assert.False(NativeInFlight, "Native storage was released before terminal completion.");
        Assert.True(_storage.Remove(storage), "Native storage was released twice.");
        Marshal.FreeHGlobal(storage);
        StorageFreed++;
        if (ThrowAfterStorageFree) throw new InvalidOperationException("Injected storage cleanup failure.");
    }
    public void Unpin(GCHandle pin)
    {
        Assert.False(NativeInFlight, "The native write still references its pin.");
        Assert.True(_pins.Remove(GCHandle.ToIntPtr(pin)), "Pin was released twice.");
        pin.Free();
        PinsFreed++;
    }
    public void Dispose()
    {
        // Keep RED failures isolated: the assertion sees a leaked acquisition,
        // then this private fixture reclaims it without any device/native I/O.
        foreach (var pin in _pins.Values) pin.Free();
        foreach (var storage in _storage) Marshal.FreeHGlobal(storage);
        _pins.Clear();
        _storage.Clear();
        ReapEntered.Dispose();
        ReleaseReap.Dispose();
    }
}
#endif
