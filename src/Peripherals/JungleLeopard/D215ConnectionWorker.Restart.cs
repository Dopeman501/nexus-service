using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Panel;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.JungleLeopard;

internal sealed partial class D215ConnectionWorker
{
    private RestartRequest? _restart;
    private (string Identity, D215DeviceInfo Info)? _lastVerified;

    internal async ValueTask<D215RestartResponse> RestartAsync(string identity, Func<bool> isAuthorized,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _stopping) != 0
            || !gate.IsEnabled(HandlerId) || !isAuthorized() || Snapshot.Status == D215ConnectionStatus.CleanupFailed)
        {
            return new("unavailable");
        }
        if (Snapshot.Status is D215ConnectionStatus.Releasing or D215ConnectionStatus.ReleaseTimedOut)
            return new("busy");
        var request = new RestartRequest(identity, isAuthorized, cancellationToken, clock.GetTimestamp());
        if (Interlocked.CompareExchange(ref _restart, request, null) is not null) return new("busy");
        ServiceLog.Info($"[{HandlerId}] explicit screen restart accepted");
        Wake();
        try { return await request.Result.WaitAsync(timing.RestartTimeout, clock).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            request.Expire();
            Wake();
            return request.CompleteRestartResult();
        }
    }

    private async Task ReconcileRestartAsync(D215Port? candidate, bool ambiguous, bool enabled, int gateVersion,
        RestartRequest request, CancellationToken stoppingToken)
    {
        if (request.Attempted && candidate is null && !ambiguous) request.ObserveDisappearance();
        bool authorized = enabled && !stoppingToken.IsCancellationRequested && request.IsAuthorized()
            && (!request.PipelineStarted || gateVersion == _observedGateVersion)
            && (!request.Caller.IsCancellationRequested || request.Attempted);
        if (!authorized || request.Expired || clock.GetElapsedTime(request.StartedAt) >= timing.RestartTimeout)
        {
            BeginRelease();
            request.CompleteRestartResult(authorized ? null : "unavailable");
            if (ObserveRetirement()) ClearRestart(request);
            return;
        }

        if (!request.PipelineStarted)
        {
            if (candidate?.Identity != request.Identity || ambiguous || _cleanupFailed)
            {
                request.CompleteRestartResult("unavailable");
                ClearRestart(request);
                return;
            }
            if (_session?.ConfirmedInfo is { } confirmed) _lastVerified = (_session.Port.Identity, confirmed);
            else if (_session is not null) _lastVerified = null;
            BeginRelease(clearIfPresent: true);
            if (!ObserveRetirement()) return;
            if (_cleanupFailed)
            {
                request.CompleteRestartResult("unavailable");
                ClearRestart(request);
                return;
            }
            _port = candidate;
            _observedGateVersion = gateVersion;
            _failures = 0;
            _lastFailure = null;
            _generation++;
            var cancellation = new CancellationTokenSource();
            var trusted = _lastVerified is { } previous && previous.Identity == request.Identity ? previous.Info : null;
            var task = Task.Run(() => RestartDeviceAsync(candidate, trusted, request, gateVersion, cancellation.Token));
            _attempt = new Attempt(cancellation, task, clock.GetTimestamp());
            request.PipelineStarted = true;
            Publish(D215ConnectionStatus.Connecting);
            _ = task.ContinueWith(_ => Wake(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return;
        }

        if (_attempt is { Completion.IsCompleted: true } completed && completed.TryFinish())
        {
            _attempt = null;
            try
            {
                _session = await completed.Completion.ConfigureAwait(false);
                _port = _session.Port;
                _lastVerified = (_port.Identity, _session.Info);
                request.Reconnected = true;
                Publish(D215ConnectionStatus.Identified);
            }
            catch (Exception ex)
            {
                RecordFailure(ex);
                request.CompleteRestartResult(ex is D215ConnectionException ? "unavailable" : null);
                ClearRestart(request);
                return;
            }
        }
        if (!request.Reconnected || _session is not { } session) return;
        if (session.StreamFailure is not null)
        {
            BeginRelease();
            request.CompleteRestartResult();
            if (ObserveRetirement()) ClearRestart(request);
            return;
        }
        if (!session.HasRunningStream) return;
        if (session.ConfirmedBrightness != request.Brightness)
        {
            request.RestoreBrightness ??= session.SetBrightnessAsync(request.Brightness,
                () => request.IsAuthorized() && gate.IsEnabled(HandlerId) && ReferenceEquals(_session, session),
                CancellationToken.None).AsTask();
            if (!request.RestoreBrightness.IsCompleted) return;
            if (!request.RestoreBrightness.IsCompletedSuccessfully || request.RestoreBrightness.Result.Status != "confirmed")
            {
                request.CompleteRestartResult();
                ClearRestart(request);
            }
            return;
        }
        _lastVerified = (session.Port.Identity, session.ConfirmedInfo!);
        request.CompleteRestartResult("restarted");
        ClearRestart(request);
    }

    private async Task<D215Session> RestartDeviceAsync(D215Port initial, D215DeviceInfo? trusted,
        RestartRequest request, int gateVersion, CancellationToken cancellationToken)
    {
        bool Authorized() => gate.IsEnabled(HandlerId) && Volatile.Read(ref _stopping) == 0
            && gateVersion == Volatile.Read(ref _gateVersion) && request.IsAuthorized()
            && !request.Expired && clock.GetElapsedTime(request.StartedAt) < timing.RestartTimeout
            && !cancellationToken.IsCancellationRequested && (!request.Caller.IsCancellationRequested || request.Attempted);
        if (!Authorized()) throw new OperationCanceledException(cancellationToken);
        var reboot = await D215Session.OpenForRestartAsync(initial, discovery, open, clock,
            timing, trusted, cancellationToken).ConfigureAwait(false);
        try
        {
            request.Brightness = reboot.ConfirmedBrightness ?? -1;
            if (request.Brightness is not (20 or 40 or 60 or 80 or 100))
                throw new InvalidOperationException("The current D215 brightness cannot be restored.");
            try { await reboot.RestartAsync(Authorized, () => request.MarkAttempted(Authorized), cancellationToken).ConfigureAwait(false); }
            catch (Exception) when (request.Attempted && !cancellationToken.IsCancellationRequested)
            {
                // USB may vanish before the write completes. Observe recovery;
                // never retransmit the possibly delivered restart command.
            }
        }
        finally
        {
            try { await reboot.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                throw new D215ConnectionException(D215ConnectionStatus.CleanupFailed,
                    "The D215 restart port release could not be confirmed.", ex);
            }
        }
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Authorized()) throw new OperationCanceledException(cancellationToken);
            var ports = discovery.Discover();
            if (!ports.Any(port => port.Identity == request.Identity)) request.ObserveDisappearance();
            if (request.Disappeared && ports.Count == 1 && ports[0].Identity == request.Identity)
                return await D215Session.ConnectAsync(ports[0], discovery, open, clock, timing, cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(25), clock, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ClearRestart(RestartRequest request) => Interlocked.CompareExchange(ref _restart, null, request);

    private sealed class RestartRequest(string identity, Func<bool> isAuthorized, CancellationToken caller, long startedAt)
    {
        private readonly TaskCompletionSource<D215RestartResponse> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _completionLock = new();
        private int _attempted;
        private int _disappeared;
        private int _expired;
        internal string Identity { get; } = identity;
        internal Func<bool> IsAuthorized { get; } = isAuthorized;
        internal CancellationToken Caller { get; } = caller;
        internal long StartedAt { get; } = startedAt;
        internal bool PipelineStarted { get; set; }
        internal bool Reconnected { get; set; }
        internal int Brightness { get; set; }
        internal Task<D215BrightnessResponse>? RestoreBrightness { get; set; }
        internal bool Attempted => Volatile.Read(ref _attempted) != 0;
        internal bool Disappeared => Volatile.Read(ref _disappeared) != 0;
        internal bool Expired => Volatile.Read(ref _expired) != 0;
        internal Task<D215RestartResponse> Result => _result.Task;
        internal void MarkAttempted(Func<bool> isAuthorized)
        {
            lock (_completionLock)
            {
                if (_result.Task.IsCompleted || Expired || !isAuthorized()) throw new OperationCanceledException();
                Volatile.Write(ref _attempted, 1);
            }
        }
        internal void ObserveDisappearance() => Volatile.Write(ref _disappeared, 1);
        internal void Expire()
        {
            lock (_completionLock) Volatile.Write(ref _expired, 1);
        }
        internal D215RestartResponse CompleteRestartResult(string? status = null)
        {
            lock (_completionLock)
            {
                var response = new D215RestartResponse(Attempted && status != "restarted" ? "uncertain" : status ?? "failed");
                if (_result.TrySetResult(response))
                    ServiceLog.Info($"[{HandlerId}] explicit screen restart ended with {response.Status}");
                return _result.Task.GetAwaiter().GetResult();
            }
        }
    }
}
