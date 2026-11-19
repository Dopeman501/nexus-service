namespace Nexus.Service.Peripherals.LianLiWireless;

// Internal-only observation contract: no request, wire or persisted schema change.
internal interface ISlv3TransportDiagnostics
{
    Slv3IoObservation LastIoObservation { get; }
}

internal enum Slv3ReadStop
{
    NotObserved,
    ExpectedLength,
    AmbiguousZeroLeading,
    ShortRead,
    EmptyRead,
    NativeError,
    Disposed,
    InvalidLength,
}

/// <summary>Numeric metadata only; no packet bytes, paths, identities or retained buffers.</summary>
internal readonly record struct Slv3IoObservation
{
    public bool Available { get; init; }
    public long Exchange { get; init; }
    public bool IsGetDev { get; init; }
    public bool FlushAttempted { get; init; }
    public bool FlushSucceeded { get; init; }
    public int FlushError { get; init; }
    public bool WriteAttempted { get; init; }
    public bool WriteSucceeded { get; init; }
    public int WriteError { get; init; }
    public int WriteRequested { get; init; }
    public uint WriteTransferred { get; init; }
    public int ExpectedRead { get; init; }
    public int ReceivedRead { get; init; }
    public long NativeReadBytes { get; init; }
    public int ReadCalls { get; init; }
    public int ShortReads { get; init; }
    public uint LastReadTransferred { get; init; }
    public Slv3ReadStop ReadStop { get; init; }
    public int ReadError { get; init; }
    public long SendStartedTicks { get; init; }
    public long SendElapsedTicks { get; init; }
    public long SendLockWaitTicks { get; init; }
    public long ReadElapsedTicks { get; init; }
    public long ReadLockWaitTicks { get; init; }
}
