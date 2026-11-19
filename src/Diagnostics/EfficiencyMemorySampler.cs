using System.Diagnostics;

namespace Nexus.Service.Diagnostics;

internal sealed class EfficiencyMemorySamplerController(Action<string> emit) : IDisposable
{
    private readonly object _gate = new();
    private EfficiencyMemorySampler? _owner;
    private long _epoch;
    private bool _retired;
    internal void Update()
    {
        lock (_gate)
        {
            if (_retired) return;
            var current = EfficiencyDiagnosticOptions.SessionRevision;
            if (_epoch == current) return;
            _owner?.Dispose(); _owner = null; _epoch = current;
            if (current != 0) _owner = EfficiencyMemorySampler.StartIfEnabled(emit);
        }
    }
    public void Dispose() { lock (_gate) { _retired = true; _owner?.Dispose(); _owner = null; _epoch = 0; } }
}

/// <summary>
/// At most 120 numeric samples, 30 seconds apart. Does not force collection or read page contents.
/// Retirement never waits for a reader/logger; an already admitted log write may finish.
/// </summary>
internal sealed class EfficiencyMemorySampler : IDisposable
{
    private const int MaximumSamples = 120;
    private readonly Func<EfficiencyMemorySnapshot> _read;
    private readonly Action<string> _emit;
    private readonly int _limit;
    private readonly long _started = Stopwatch.GetTimestamp();
    private Timer? _timer;
    private int _busy;
    private int _attempts;
    private int _retired;
    private long _leaseEpoch;

    internal EfficiencyMemorySampler(Func<EfficiencyMemorySnapshot> read, Action<string> emit, int limit = MaximumSamples)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(emit);
        if (limit is < 1 or > MaximumSamples) throw new ArgumentOutOfRangeException(nameof(limit));
        _read = read;
        _emit = emit;
        _limit = limit;
    }

    internal bool IsRetired => Volatile.Read(ref _retired) != 0;

    internal static EfficiencyMemorySampler? StartIfEnabled(Action<string> emit)
    {
        var epoch = EfficiencyDiagnosticOptions.SessionRevision;
        if (epoch == 0) return null;
        var owner = new EfficiencyMemorySampler(ReadCurrentProcess, emit);
        owner._leaseEpoch = epoch;
        owner._timer = new Timer(static state => ((EfficiencyMemorySampler)state!).Tick(), owner, 30_000, 30_000);
        owner.Tick();
        return owner;
    }

    internal void Tick()
    {
        if (_leaseEpoch != 0 && _leaseEpoch != EfficiencyDiagnosticOptions.SessionRevision) { Dispose(); return; }
        if (IsRetired || Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            if (IsRetired) return;
            var sequence = ++_attempts;
            string line;
            try { line = _read().Format(sequence, (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds); }
            catch (Exception)
            {
                // Exception text may contain paths or other private context; this is numeric telemetry only.
                line = FormattableString.Invariant($"[efficiency-memory] sample={sequence} unavailable=1");
            }
            if (!IsRetired && (_leaseEpoch == 0 || _leaseEpoch == EfficiencyDiagnosticOptions.SessionRevision)) TryEmit(line);
            if (sequence >= _limit) Dispose();
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private void TryEmit(string line)
    {
        try { _emit(line); }
        catch (Exception)
        {
            // No working diagnostic sink remains. Stop instead of repeatedly attempting failed I/O.
            Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _retired, 1) != 0) return;
        _timer?.Dispose();
    }

    internal static EfficiencyMemorySnapshot ReadCurrentProcess()
    {
        var gc = GC.GetGCMemoryInfo();
        using var process = Process.GetCurrentProcess();
        return new(GC.GetTotalAllocatedBytes(precise: false), GC.GetTotalMemory(forceFullCollection: false),
            gc.Index, gc.HeapSizeBytes, gc.TotalCommittedBytes, gc.FragmentedBytes,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
            process.PrivateMemorySize64, process.WorkingSet64, process.HandleCount);
    }
}

internal readonly record struct EfficiencyMemorySnapshot(long AllocatedBytes, long ManagedApproxBytes, long LastGcIndex,
    long LastGcHeapBytes, long LastGcCommittedBytes, long LastGcFragmentedBytes, int Gen0, int Gen1, int Gen2,
    long PrivateBytes, long WorkingSetBytes, int Handles)
{
    internal string Format(int sequence, long elapsedMs) => FormattableString.Invariant(
        $"[efficiency-memory] sample={sequence} elapsedMs={elapsedMs} allocatedBytes={AllocatedBytes} managedApproxBytes={ManagedApproxBytes} lastGcKnown={(LastGcIndex > 0 ? 1 : 0)} lastGcIndex={LastGcIndex} lastGcHeapBytes={LastGcHeapBytes} lastGcCommittedBytes={LastGcCommittedBytes} lastGcFragmentedBytes={LastGcFragmentedBytes} gen0={Gen0} gen1={Gen1} gen2={Gen2} privateBytes={PrivateBytes} workingSetBytes={WorkingSetBytes} handles={Handles}");
}

