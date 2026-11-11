namespace Nexus.Service.Routes;

/// <summary>One constant-size host budget, shared by reads and writes before parsing or inventory work.</summary>
internal sealed class AudioTriggerRequestBudget
{
    internal const int MaximumPerSecond = 128;
    private readonly object _gate = new();
    private readonly Func<long> _now;
    private long _windowEnd;
    private int _requests;
    internal AudioTriggerRequestBudget(Func<long>? now = null) => _now = now ?? (() => Environment.TickCount64);
    internal bool TryAccept()
    {
        lock (_gate)
        {
            var now = _now();
            if (now >= _windowEnd) { _requests = 0; _windowEnd = now + 1000; }
            if (_requests >= MaximumPerSecond) return false;
            _requests++;
            return true;
        }
    }
}
