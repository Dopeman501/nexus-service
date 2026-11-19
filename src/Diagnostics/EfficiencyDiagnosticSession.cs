using System;
using System.Text.Json.Serialization;

namespace Nexus.Service.Diagnostics;

public sealed class EfficiencyLeaseProjection
{
    public string ServiceGeneration { get; set; } = "";
    public long Revision { get; set; }
    public string? RunId { get; set; }
    public int RemainingMs { get; set; }
    public string? ExpiresUtc { get; set; }
    public bool NativeActive { get; set; }
    public string? OverlayInstance { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class EfficiencyLeaseRequest
{
    public string ExpectedServiceGeneration { get; set; } = "";
    public long ExpectedRevision { get; set; }
    public string RunId { get; set; } = "";
    public int DurationSeconds { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class EfficiencyLeaseAck
{
    public string ServiceGeneration { get; set; } = "";
    public long Revision { get; set; }
    public string RunId { get; set; } = "";
    public string OverlayInstance { get; set; } = "";
}

/// <summary>One bounded nonrenewable lease. All state is process memory; no device or preference access.</summary>
internal sealed class EfficiencyDiagnosticSession : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<long> _clock;
    private readonly Func<DateTimeOffset> _utc;
    private readonly Action<EfficiencyLeaseProjection>? _apply;
    private readonly bool _schedule;
    private Timer? _timer;
    private string? _runId, _lastRunId, _overlay;
    private int _duration;
    private long _deadline, _revision, _lastRequest = long.MinValue;
    private string? _expiresUtc;
    private bool _disposed;
    internal string Generation { get; } = Guid.NewGuid().ToString("N");

    internal EfficiencyDiagnosticSession(Func<long>? clock = null, Func<DateTimeOffset>? utc = null,
        Action<EfficiencyLeaseProjection>? apply = null, bool schedule = true)
    {
        _clock = clock ?? (() => Environment.TickCount64);
        _utc = utc ?? (() => DateTimeOffset.UtcNow);
        _apply = apply; _schedule = schedule;
    }

    internal bool AcceptRequest()
    {
        lock (_gate)
        {
            var now = _clock();
            if (_lastRequest != long.MinValue && now - _lastRequest < 250) return false;
            _lastRequest = now; return true;
        }
    }

    internal EfficiencyLeaseProjection Snapshot()
    {
        lock (_gate) { ExpireLocked(); return ProjectionLocked(); }
    }

    internal int Start(EfficiencyLeaseRequest body, out EfficiencyLeaseProjection state)
    {
        lock (_gate)
        {
            ExpireLocked();
            if (!DiagnosticLeaseGate.IsId(body.RunId) || !DiagnosticLeaseGate.IsId(body.ExpectedServiceGeneration)
                || body.ExpectedRevision < 0 || body.DurationSeconds is < 30 or > 2100)
                return Reply(400, out state);
            if (body.ExpectedServiceGeneration != Generation) return Reply(409, out state);
            if (_runId == body.RunId && _duration == body.DurationSeconds) return Reply(200, out state);
            if (body.ExpectedRevision != _revision || _runId is not null || _lastRunId == body.RunId || _disposed)
                return Reply(409, out state);
            _runId = _lastRunId = body.RunId; _duration = body.DurationSeconds;
            _deadline = _clock() + body.DurationSeconds * 1000L;
            _expiresUtc = _utc().AddSeconds(body.DurationSeconds).ToString("o");
            _overlay = null; _revision++;
            if (!PublishLocked()) return Reply(503, out state);
            if (_schedule) _timer = new Timer(_ => Tick(), null, 1000, 1000);
            return Reply(200, out state);
        }
    }

    internal int Cancel(EfficiencyLeaseRequest body, out EfficiencyLeaseProjection state)
    {
        lock (_gate)
        {
            ExpireLocked();
            if (body.ExpectedServiceGeneration != Generation || body.ExpectedRevision != _revision
                || body.RunId != _runId || _runId is null) return Reply(409, out state);
            RetireLocked(); return Reply(200, out state);
        }
    }

    internal int Acknowledge(EfficiencyLeaseAck body)
    {
        lock (_gate)
        {
            ExpireLocked();
            if (body.ServiceGeneration != Generation || body.Revision != _revision || body.RunId != _runId
                || _runId is null || !DiagnosticLeaseGate.IsId(body.OverlayInstance)) return 409;
            if (_overlay is not null && _overlay != body.OverlayInstance) return 409;
            _overlay = body.OverlayInstance; return 200;
        }
    }

    private int Reply(int status, out EfficiencyLeaseProjection state) { state = ProjectionLocked(); return status; }
    private EfficiencyLeaseProjection ProjectionLocked() => new()
    {
        ServiceGeneration = Generation, Revision = _revision, RunId = _runId,
        RemainingMs = _runId is null ? 0 : (int)Math.Clamp(_deadline - _clock(), 0, DiagnosticLeaseGate.MaximumMilliseconds),
        ExpiresUtc = _runId is null ? null : _expiresUtc, NativeActive = _runId is not null, OverlayInstance = _overlay,
    };
    private void ExpireLocked() { if (_runId is not null && _clock() >= _deadline) RetireLocked(); }
    private void RetireLocked()
    {
        _runId = null; _overlay = null; _revision++; _timer?.Dispose(); _timer = null; PublishLocked();
    }
    private bool PublishLocked()
    {
        try { _apply?.Invoke(ProjectionLocked()); return true; }
        catch (Exception)
        {
            // Diagnostics never destabilize the host. Retire a partially applied
            // activation and make one best-effort disabling notification.
            if (_runId is not null)
            {
                _runId = null; _overlay = null; _revision++; _timer?.Dispose(); _timer = null;
                try { _apply?.Invoke(ProjectionLocked()); } catch (Exception) { }
            }
            return false;
        }
    }
    private void Tick() { lock (_gate) ExpireLocked(); }
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; if (_runId is not null) RetireLocked(); } }
}
