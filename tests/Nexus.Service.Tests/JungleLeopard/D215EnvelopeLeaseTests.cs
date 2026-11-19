using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Nexus.Service.Peripherals.JungleLeopard;

namespace Nexus.Service.Tests.JungleLeopard;

public sealed class D215EnvelopeLeaseTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(32 * 1024)]
    [InlineData(100 * 1024)]
    [InlineData(305 * 1024)]
    public void Span_codec_matches_owned_golden_bytes_without_touching_the_unused_destination(int count)
    {
        var jpeg = Enumerable.Repeat((byte)255, count).ToArray();
        var destination = Enumerable.Repeat((byte)31, count + 10).ToArray();
        var length = D215Protocol.WriteJpegEnvelope(jpeg, destination);
        Assert.Equal(count + 6, length);
        Assert.Equal(count, (int)BinaryPrimitives.ReadUInt32LittleEndian(destination));
        Assert.Equal(D215Protocol.BuildJpegEnvelope(jpeg), destination[..length]);
        Assert.Equal(new byte[] { 31, 31, 31, 31 }, destination[length..]);
    }

    [Fact]
    public void Codec_rejects_invalid_input_and_short_storage_before_writing()
    {
        var bytes = new byte[] { 17, 17, 17, 17, 17, 17 };
        Assert.Throws<ArgumentOutOfRangeException>(() => D215Protocol.WriteJpegEnvelope([], bytes));
        Assert.Throws<ArgumentOutOfRangeException>(() => D215Protocol.WriteJpegEnvelope(new byte[305 * 1024 + 1], bytes));
        Assert.Throws<ArgumentException>(() => D215Protocol.WriteJpegEnvelope([1], bytes));
        Assert.All(bytes, value => Assert.Equal(17, value));
    }

    [Fact]
    public void Two_exact_slots_remain_independent_and_a_third_rental_never_allocates()
    {
        using var pool = new D215EnvelopePool();
        Assert.Equal(624652, D215EnvelopePool.CapacityBytes);
        Assert.True(pool.TryRent(out var firstReservation));
        using var first = firstReservation.Format([1, 2]);
        Assert.Throws<InvalidOperationException>(() => first.Format([7, 8]));
        Assert.True(pool.TryRent(out var secondReservation));
        using var second = secondReservation.Format([3, 4]);
        Assert.True(MemoryMarshal.TryGetArray(first.Memory, out var a));
        Assert.True(MemoryMarshal.TryGetArray(second.Memory, out var b));
        Assert.Equal(312326, a.Array!.Length);
        Assert.Equal(312326, b.Array!.Length);
        Assert.NotSame(a.Array, b.Array);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var rented = pool.TryRent(out _);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.False(rented);
        Assert.Equal(D215Protocol.BuildJpegEnvelope([1, 2]), first.Memory.ToArray());
        Assert.Equal(D215Protocol.BuildJpegEnvelope([3, 4]), second.Memory.ToArray());
    }

    [Fact]
    public void Copied_old_lease_release_is_idempotent_and_cannot_release_a_reused_slot()
    {
        using var pool = new D215EnvelopePool();
        Assert.True(pool.TryRent(out var reservation));
        var first = reservation.Format([1]);
        var copy = first;
        first.Dispose();
        Assert.True(pool.TryRent(out var nextReservation));
        using var next = nextReservation.Format([2]);
        copy.Dispose();
        Assert.True(pool.TryRent(out var spare));
        Assert.False(pool.TryRent(out _));
        Assert.Throws<ObjectDisposedException>(() => copy.Format([7]));
        spare.Dispose();
        Assert.Equal(D215Protocol.BuildJpegEnvelope([2]), next.Memory.ToArray());
    }

    [Fact]
    public void Retirement_preserves_a_native_lease_but_never_readmits_a_slot()
    {
        var pool = new D215EnvelopePool();
        Assert.True(pool.TryRent(out var reservation));
        using var pending = reservation.Format([1, 2]);
        pool.Dispose();
        Assert.False(pool.TryRent(out _));
        Assert.Equal(D215Protocol.BuildJpegEnvelope([1, 2]), pending.Memory.ToArray());
        pending.Dispose();
        Assert.False(pool.TryRent(out _));
        pool.Dispose();
        var owned = D215EnvelopeLease.FromOwned(new byte[] { 1, 2 });
        owned.Dispose();
        Assert.Equal(new byte[] { 1, 2 }, owned.Memory.ToArray());
    }
}
