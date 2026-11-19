using Nexus.Service.Peripherals.JungleLeopard;

namespace Nexus.Service.Tests.JungleLeopard;

internal static class D215EnvelopeTestExtensions
{
    // Existing golden-vector tests explicitly copy before returning the lease.
    internal static ReadOnlyMemory<byte> TakeEnvelopeSnapshot(this D215BgraFrames frames)
    {
        using var envelope = frames.TakeLatestEnvelope();
        return envelope.Memory.ToArray();
    }
}
