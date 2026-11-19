using System;

namespace Nexus.Service.Peripherals.JungleLeopard;

/// <summary>One immutable envelope until release. Copies share the same idempotent, generation-bound release.</summary>
internal readonly struct D215EnvelopeLease : IDisposable
{
    private readonly D215EnvelopePool? _owner;
    private readonly int _slot;
    private readonly long _generation;
    public ReadOnlyMemory<byte> Memory { get; }
    public bool IsEmpty => Memory.IsEmpty;
    public int Length => Memory.Length;

    internal D215EnvelopeLease(D215EnvelopePool? owner, int slot, long generation, ReadOnlyMemory<byte> memory)
    {
        _owner = owner;
        _slot = slot;
        _generation = generation;
        Memory = memory;
    }

    /// <summary>Test/source adapters can transfer independently owned immutable storage without a pool.</summary>
    internal static D215EnvelopeLease FromOwned(ReadOnlyMemory<byte> memory) => new(null, 0, 0, memory);

    internal D215EnvelopeLease Format(ReadOnlySpan<byte> jpeg) => _owner!.Format(_slot, _generation, jpeg);
    public void Dispose() => _owner?.Release(_slot, _generation);
}

/// <summary>Two exact maximum-sized slots, including retained/in-flight leases. Exhaustion never allocates.</summary>
internal sealed class D215EnvelopePool : IDisposable
{
    internal const int SlotCount = 2;
    internal const int CapacityBytes = SlotCount * D215Protocol.MaximumJpegEnvelopeBytes;
    private readonly object _gate = new();
    private readonly byte[][] _buffers = [new byte[D215Protocol.MaximumJpegEnvelopeBytes], new byte[D215Protocol.MaximumJpegEnvelopeBytes]];
    private readonly long[] _generations = new long[SlotCount];
    private readonly bool[] _leased = new bool[SlotCount];
    private readonly bool[] _formatted = new bool[SlotCount];
    private bool _retired;

    internal int LeasedCapacityBytes
    {
        get
        {
            lock (_gate)
            {
                var slots = 0;
                for (var slot = 0; slot < SlotCount; slot++) if (_leased[slot]) slots++;
                return slots * D215Protocol.MaximumJpegEnvelopeBytes;
            }
        }
    }

    internal bool TryRent(out D215EnvelopeLease lease) => TryRent(out lease, out _);

    internal bool TryRent(out D215EnvelopeLease lease, out bool retired)
    {
        lock (_gate)
        {
            retired = _retired;
            if (!_retired)
            {
                for (var slot = 0; slot < SlotCount; slot++)
                {
                    if (_leased[slot]) continue;
                    _leased[slot] = true;
                    _formatted[slot] = false;
                    lease = new(this, slot, ++_generations[slot], default);
                    return true;
                }
            }
            lease = default;
            return false;
        }
    }

    internal D215EnvelopeLease Format(int slot, long generation, ReadOnlySpan<byte> jpeg)
    {
        lock (_gate)
        {
            if (!_leased[slot] || _generations[slot] != generation) throw new ObjectDisposedException(nameof(D215EnvelopeLease));
            if (_formatted[slot]) throw new InvalidOperationException("A published envelope lease is immutable until release.");
            var length = D215Protocol.WriteJpegEnvelope(jpeg, _buffers[slot]);
            _formatted[slot] = true;
            return new(this, slot, generation, _buffers[slot].AsMemory(0, length));
        }
    }

    internal void Release(int slot, long generation)
    {
        lock (_gate)
        {
            if (_generations[slot] == generation) _leased[slot] = false;
        }
    }

    public void Dispose() { lock (_gate) _retired = true; }
}
