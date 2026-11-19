using System;
using System.Collections.Generic;
using System.Threading;

namespace Nexus.Service.Diagnostics;

/// <summary>Ephemeral, process-local diagnostics. No persisted preferences or environment writes.</summary>
internal static class EfficiencyDiagnosticOptions
{
    private static readonly DiagnosticLeaseGate Runtime = new();
    private static bool _runtimeApplied;
    private static readonly long LegacyDeadline = IsEnabled(Environment.GetEnvironmentVariable("NEXUS_EFFICIENCY_DIAGNOSTICS"))
        ? Environment.TickCount64 + DiagnosticLeaseGate.MaximumMilliseconds : 0;
    internal static bool Enabled => SessionRevision != 0;
    internal static long SessionRevision => Volatile.Read(ref _runtimeApplied) ? Runtime.SessionRevision
        : LegacyDeadline > Environment.TickCount64 ? long.MaxValue : 0;
    internal static bool IsEnabled(string? value) => string.Equals(value, "1", StringComparison.Ordinal);
    internal static bool Apply(string authority, long revision, string? runId, int remainingMs)
    {
        if (!Runtime.Apply(authority, revision, runId, remainingMs)) return false;
        Volatile.Write(ref _runtimeApplied, true);
        return true;
    }
    internal static void Deactivate()
    {
        Runtime.Deactivate();
        Volatile.Write(ref _runtimeApplied, true);
    }
}

/// <summary>Monotonic first-sight deadline; repeated projection cannot renew or revive a run.</summary>
internal sealed class DiagnosticLeaseGate(Func<long>? clock = null)
{
    internal const int MaximumMilliseconds = 2_100_000;
    private readonly object _gate = new();
    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private string? _authority, _runId;
    private readonly Queue<string> _retiredAuthorities = new();
    private long _revision = -1, _deadline, _epoch;
    internal long SessionRevision
    {
        get { lock (_gate) return _runId is not null && _clock() < _deadline ? _epoch : 0; }
    }
    internal bool Apply(string authority, long revision, string? runId, int remainingMs)
    {
        if (!IsId(authority) || revision < 0 || remainingMs < 0 || remainingMs > MaximumMilliseconds
            || (runId is not null && !IsId(runId)) || (runId is null && remainingMs != 0)) return false;
        lock (_gate)
        {
            if (_authority != authority && _retiredAuthorities.Contains(authority)) return false;
            if (_authority == authority && revision < _revision) return false;
            var now = _clock();
            if (_authority == authority && _runId == runId)
            {
                _revision = revision;
                if (runId is null) return true;
                if (now >= _deadline) return false;
                _deadline = Math.Min(_deadline, now + remainingMs);
                return true;
            }
            if (_authority is not null && _authority != authority)
            {
                _retiredAuthorities.Enqueue(_authority);
                if (_retiredAuthorities.Count > 8) _retiredAuthorities.Dequeue();
            }
            _authority = authority; _revision = revision; _runId = runId;
            _deadline = now + remainingMs;
            _epoch++;
            return true;
        }
    }
    internal static bool IsId(string? value)
    {
        if (value is null || value.Length != 32) return false;
        foreach (var c in value) if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }
    internal void Deactivate()
    {
        lock (_gate) { if (_runId is not null) { _runId = null; _deadline = 0; _revision++; } }
    }
}

/// <summary>One lazily admitted counter per active epoch, never a timer or an I/O owner.</summary>
internal sealed class DiagnosticLeaseOwner<T> where T : class
{
    private readonly object _gate = new();
    private long _epoch;
    private T? _owner;
    internal T? Get(Func<T> create)
    {
        var epoch = EfficiencyDiagnosticOptions.SessionRevision;
        if (epoch == 0)
        {
            if (_owner is not null) lock (_gate)
            {
                if (EfficiencyDiagnosticOptions.SessionRevision == 0) { _owner = null; _epoch = 0; }
            }
            return null;
        }
        lock (_gate)
        {
            if (epoch != EfficiencyDiagnosticOptions.SessionRevision) return null;
            if (_epoch != epoch) { _owner = create(); _epoch = epoch; }
            if (epoch != EfficiencyDiagnosticOptions.SessionRevision) { _owner = null; _epoch = 0; return null; }
            return _owner;
        }
    }
}
