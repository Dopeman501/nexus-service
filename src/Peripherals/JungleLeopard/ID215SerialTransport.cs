using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Peripherals.JungleLeopard;

/// <summary>One exclusively opened port. A zero-byte read means the port is idle.</summary>
internal interface ID215SerialTransport : IAsyncDisposable
{
    ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken);
    ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken);
}
