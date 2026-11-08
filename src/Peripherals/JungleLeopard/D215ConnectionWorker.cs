using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.JungleLeopard;

/// <summary>Single owner of the D215 port lease. Discovery and UI never open the port.</summary>
internal sealed partial class D215ConnectionWorker(
    ID215PortDiscovery discovery,
    DeviceControlGate gate,
    Func<D215Port, CancellationToken, ValueTask<ID215SerialTransport>> open,
    TimeProvider clock,
    D215Timing timing,
    TimeSpan pollInterval) : BackgroundService
{
    internal const string HandlerId = JungleLeopardD215Handler.HandlerId;
    internal static TimeSpan DefaultPollInterval { get; } = TimeSpan.FromSeconds(1);
    private const int MaximumAttempts = 2;

    private readonly SemaphoreSlim _wake = new(0, 1);
    private D215ConnectionSnapshot _snapshot = new(null, null, D215ConnectionStatus.Absent);
    private D215Port? _port;
    private volatile D215Session? _session;
    private volatile Attempt? _attempt;
    private Retirement? _retirement;
    private int _gateVersion;
    private int _observedGateVersion;
    private int _failures;
    private long? _lastFailure;
    private long _generation;
    private bool _cleanupFailed;
    private int _stopping;

    internal D215ConnectionSnapshot Snapshot => Volatile.Read(ref _snapshot);

    internal D215Session? GetIdentifiedSession(string identity)
    {
        var session = _session;
        return session is not null && Snapshot.Status == D215ConnectionStatus.Identified
            && string.Equals(session.Port.Identity, identity, StringComparison.Ordinal)
            && Volatile.Read(ref _stopping) == 0 && ClaimAllowed(CancellationToken.None) && session.CanStream ? session : null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var stopPublishing = stoppingToken.Register(() =>
        {
            Volatile.Write(ref _stopping, 1);
            _session?.RequestStreamStop();
        });
        gate.Changed += OnGateChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await ReconcileAsync(stoppingToken).ConfigureAwait(false);
                await _wake.WaitAsync(pollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            gate.Changed -= OnGateChanged;
            Interlocked.Exchange(ref _restart, null)?.CompleteRestartResult();
            BeginRelease(clearIfPresent: _port is not null);
            if (_retirement is { } retirement)
            {
                try { await retirement.Task.WaitAsync(RemainingReleaseTime(retirement), clock).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    // The same task still owns cleanup if Windows returns after shutdown.
                    BeginRelease();
                }
            }
            if (ObserveRetirement())
            {
                _port = null;
                Publish(_cleanupFailed ? D215ConnectionStatus.CleanupFailed : D215ConnectionStatus.Absent);
            }
        }
    }

    private async Task ReconcileAsync(CancellationToken stoppingToken)
    {
        D215Port? candidate;
        bool ambiguous;
        try
        {
            var candidates = discovery.Discover();
            candidate = candidates.Count == 1 ? candidates[0] : null;
            ambiguous = candidates.Count > 1;
        }
        catch (Exception ex)
        {
            if (Snapshot.Status != D215ConnectionStatus.IoFailure)
                ServiceLog.Warn($"[{HandlerId}] discovery failed ({ex.GetType().Name})");
            BeginRelease();
            if (ObserveRetirement())
                Publish(_cleanupFailed ? D215ConnectionStatus.CleanupFailed : D215ConnectionStatus.IoFailure);
            return;
        }

        int gateVersion = Volatile.Read(ref _gateVersion);
        bool enabled = gate.IsEnabled(HandlerId);
        if (Volatile.Read(ref _restart) is { } restart)
        {
            await ReconcileRestartAsync(candidate, ambiguous, enabled, gateVersion, restart, stoppingToken).ConfigureAwait(false);
            return;
        }
        if (candidate != _port || gateVersion != _observedGateVersion || !enabled)
        {
            BeginRelease(clearIfPresent: candidate is not null && candidate == _port);
            _port = candidate;
            _observedGateVersion = gateVersion;
            _failures = 0;
            _lastFailure = null;
        }

        if (!ObserveRetirement()) return;

        if (_cleanupFailed)
            Publish(D215ConnectionStatus.CleanupFailed);
        else if (candidate is null)
            Publish(ambiguous ? D215ConnectionStatus.Ambiguous : D215ConnectionStatus.Absent);
        else if (!enabled)
            Publish(D215ConnectionStatus.Available);
        else
            await ConnectIfNeededAsync(candidate, stoppingToken).ConfigureAwait(false);
    }

    private async Task ConnectIfNeededAsync(D215Port candidate, CancellationToken stoppingToken)
    {
        if (_session?.StreamFailure is { } streamFailure)
        {
            BeginRelease(waitingStatus: StatusFor(streamFailure));
            RecordFailure(streamFailure);
            if (!ObserveRetirement()) return;
            if (_cleanupFailed)
            {
                Publish(D215ConnectionStatus.CleanupFailed);
                return;
            }
        }

        if (_attempt is { Completion.IsCompleted: false } active
            && clock.GetElapsedTime(active.StartedAt) >= timing.IdentificationTimeout)
        {
            BeginRelease(waitingStatus: D215ConnectionStatus.NoResponse);
            RecordFailure(new D215ConnectionException(D215ConnectionStatus.NoResponse, "The driver did not finish identifying the D215."));
            ObserveRetirement();
            return;
        }

        if (_attempt is { Completion.IsCompleted: true } completed)
        {
            if (!ClaimAllowed(stoppingToken) || !completed.TryFinish())
            {
                BeginRelease();
                ObserveRetirement();
                return;
            }

            _attempt = null;
            try
            {
                _session = await completed.Completion.ConfigureAwait(false);
                if (!ClaimAllowed(stoppingToken))
                {
                    BeginRelease();
                    ObserveRetirement();
                    return;
                }
                Publish(D215ConnectionStatus.Identified);
                _lastVerified = (_session.Port.Identity, _session.Info);
                ServiceLog.Info($"[{HandlerId}] generation {_generation} identified");
            }
            catch (OperationCanceledException) when (!ClaimAllowed(stoppingToken)) { }
            catch (Exception ex)
            {
                RecordFailure(ex);
            }
        }

        if (_attempt is not null || _session is not null || _failures >= MaximumAttempts
            || (_lastFailure is { } failedAt && clock.GetElapsedTime(failedAt) < pollInterval)
            || !ClaimAllowed(stoppingToken))
        {
            return;
        }

        _generation++;
        // Host cancellation must not run a native driver's cancellation callback inline.
        var cancellation = new CancellationTokenSource();
        var startedAt = clock.GetTimestamp();
        var task = Task.Run(async () =>
        {
            if (!ClaimAllowed(stoppingToken))
                throw new OperationCanceledException(cancellation.Token);
            return await D215Session.ConnectAsync(candidate, discovery, open, clock, timing, cancellation.Token).ConfigureAwait(false);
        });
        var attempt = new Attempt(cancellation, task, startedAt);
        _attempt = attempt;
        if (!ClaimAllowed(stoppingToken)) _ = attempt.Cancel();
        Publish(D215ConnectionStatus.Connecting);
        _ = task.ContinueWith(_ => Wake(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void RecordFailure(Exception failure)
    {
        var status = StatusFor(failure);
        _cleanupFailed = status == D215ConnectionStatus.CleanupFailed;
        _lastFailure = clock.GetTimestamp();
        _failures = status is D215ConnectionStatus.Unsupported or D215ConnectionStatus.InvalidReply or D215ConnectionStatus.CleanupFailed
            ? MaximumAttempts : _failures + 1;
        Publish(status);
        ServiceLog.Warn($"[{HandlerId}] generation {_generation} ended with {status} ({failure.GetType().Name})");
    }

    private void BeginRelease(bool clearIfPresent = false, D215ConnectionStatus waitingStatus = D215ConnectionStatus.Releasing)
    {
        if (_retirement is { } retiring)
        {
            if (!clearIfPresent && retiring.Session is { } closing)
                _ = closing.CloseAsync(clearIfPresent: false);
            return;
        }

        var pending = _attempt;
        var session = _session;
        if (pending is null && session is null) return;
        if (session?.ConfirmedInfo is { } confirmed) _lastVerified = (session.Port.Identity, confirmed);
        else if (session is not null) _lastVerified = null;
        var startedAt = clock.GetTimestamp();
        // Signal a graceful clear or abort before revoking the pump's active session.
        var task = RetireLeaseAsync(pending, session, clearIfPresent, _generation);
        _attempt = null;
        _session = null;
        _retirement = new Retirement(task, session, startedAt, waitingStatus);
        _ = task.ContinueWith(_ => Wake(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private bool ObserveRetirement()
    {
        if (_retirement is not { } retirement) return true;
        if (!retirement.Task.IsCompleted)
        {
            var status = RemainingReleaseTime(retirement) == TimeSpan.Zero
                ? D215ConnectionStatus.ReleaseTimedOut : retirement.WaitingStatus;
            if (status == D215ConnectionStatus.ReleaseTimedOut && Snapshot.Status != status)
            {
                ServiceLog.Warn($"[{HandlerId}] generation {_generation} still owns the port after the release deadline");
                BeginRelease();
            }
            Publish(status);
            return false;
        }

        _cleanupFailed |= retirement.Task.GetAwaiter().GetResult();
        _retirement = null;
        return true;
    }

    private TimeSpan RemainingReleaseTime(Retirement retirement)
    {
        var remaining = timing.ReleaseTimeout - clock.GetElapsedTime(retirement.StartedAt);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static async Task<bool> RetireLeaseAsync(Attempt? pending, D215Session? session, bool clearIfPresent, long generation)
    {
        var cleanupFailed = false;
        if (pending is not null)
        {
            await pending.Cancel().ConfigureAwait(false);
            try
            {
                // A late open still owns the lease until the worker observes its cleanup.
                session = await pending.Completion.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                cleanupFailed = StatusFor(ex) == D215ConnectionStatus.CleanupFailed;
                ServiceLog.Warn($"[{HandlerId}] generation {generation} ended during release with {StatusFor(ex)}");
            }
            finally { pending.TryFinish(); }
        }

        if (session is not null)
        {
            try
            {
                await session.CloseAsync(clearIfPresent).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                cleanupFailed = true;
                ServiceLog.Error($"[{HandlerId}] generation {generation} release failed ({ex.GetType().Name})");
            }
        }
        return cleanupFailed;
    }

    private static D215ConnectionStatus StatusFor(Exception ex) => ex switch
    {
        D215ConnectionException failure => failure.Status,
        UnauthorizedAccessException => D215ConnectionStatus.Busy,
        _ => D215ConnectionStatus.IoFailure,
    };

    private void OnGateChanged(string handlerId, bool enabled)
    {
        if (!string.Equals(handlerId, HandlerId, StringComparison.OrdinalIgnoreCase)) return;
        Interlocked.Increment(ref _gateVersion);
        if (_attempt is { } pending) _ = pending.Cancel();
        _session?.RequestStreamStop();
        Wake();
    }

    private bool ClaimAllowed(CancellationToken stoppingToken) => !stoppingToken.IsCancellationRequested
        && Volatile.Read(ref _gateVersion) == _observedGateVersion && gate.IsEnabled(HandlerId);

    private static async Task CancelAsync(CancellationTokenSource cancellation)
    {
        try { await cancellation.CancelAsync().ConfigureAwait(false); }
        catch (Exception ex) { ServiceLog.Warn($"[{HandlerId}] cancellation callback failed ({ex.GetType().Name})"); }
    }

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private void Publish(D215ConnectionStatus status) =>
        Volatile.Write(ref _snapshot, new D215ConnectionSnapshot(_port, _session?.Info, status));

    private sealed class Attempt(CancellationTokenSource cancellation, Task<D215Session> task, long startedAt)
    {
        private readonly object _lock = new();
        private CancellationTokenSource? _cancellation = cancellation;
        private Task? _cancelTask;

        internal Task<D215Session> Completion { get; } = task;
        internal long StartedAt { get; } = startedAt;

        internal Task Cancel()
        {
            lock (_lock)
                return _cancellation is { } source ? _cancelTask ??= CancelAsync(source) : Task.CompletedTask;
        }

        internal bool TryFinish()
        {
            lock (_lock)
            {
                if (_cancelTask is { IsCompleted: false }) return false;
                _cancellation?.Dispose();
                _cancellation = null;
                return true;
            }
        }
    }

    private sealed record Retirement(Task<bool> Task, D215Session? Session, long StartedAt, D215ConnectionStatus WaitingStatus);
}
