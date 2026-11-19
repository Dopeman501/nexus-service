using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting.Rgb;

public sealed partial class RgbBridge
{
    // Exclusion reconciliation is a refresh continuation, including its deferred
    // rescan/restart. A closed socket may still fall back in its own generation.
    private void RequireRedetectionOwner(RefreshOwner owner)
    {
        if (!OwnsRefreshScope(owner)) throw new RefreshRetiredException();
    }

    private async Task RunRefreshRedetectionAsync(string reason, bool detectorsChanged, RefreshOwner owner)
    {
        try
        {
            if (detectorsChanged)
            {
                string? detectors = null;
                if (!PublishRefresh(owner, () => detectors = _proc.RefreshDetectorOverrides())) return;
                RequireRedetectionOwner(owner);
                bool? accepted = detectors is null ? false
                    : _controller is IOpenRgbRefreshTransport transport
                        ? await transport.TrySetSettingsAsync("Detectors", detectors, owner.Generation,
                            () => OwnsRefreshScope(owner), owner.Cancellation).ConfigureAwait(false)
                        : await _controller.SetSettingsAsync("Detectors", detectors, owner.Cancellation).ConfigureAwait(false);
                RequireRedetectionOwner(owner);
                if (accepted is null) return;
                if (!accepted.Value)
                {
                    await BounceForRefreshAsync(reason, owner).ConfigureAwait(false);
                    return;
                }
            }
            RequireRedetectionOwner(owner);
            bool? rescanned = _controller is IOpenRgbRefreshTransport rescanTransport
                ? await rescanTransport.TryRescanAsync(owner.Generation,
                    () => OwnsRefreshScope(owner), owner.Cancellation).ConfigureAwait(false)
                : await _controller.RescanAsync(owner.Cancellation).ConfigureAwait(false);
            RequireRedetectionOwner(owner);
            if (rescanned == false) await BounceForRefreshAsync(reason, owner).ConfigureAwait(false);
        }
        catch (Exception) when (!OwnsRefreshScope(owner)) { }
        catch (Exception ex)
        {
            PublishRefresh(owner, () => ServiceLog.Warn($"[rgb-bridge] re-detect ({reason}) failed: {ex.Message}; restarting the daemon"),
                requireConnected: false);
            await BounceForRefreshAsync(reason, owner).ConfigureAwait(false);
        }
    }

    private void BounceForRedetection(string reason, RefreshOwner? owner)
    {
        if (owner is { } scoped) RedetectionBounceTask = Task.Run(() => BounceForRefreshAsync(reason, scoped));
        else BounceSubprocess(reason);
    }

    private async Task BounceForRefreshAsync(string reason, RefreshOwner owner)
    {
        try
        {
            var stopped = false;
            PublishRefresh(owner, () =>
            {
                if (RefuseRestartDuringShutdown(reason)) return;
                Task? pendingStart;
                lock (_lock) pendingStart = _pendingStart;
                if (pendingStart is { IsCompleted: false }) return;
                if (BounceOverride is { } bounce) { bounce(reason); return; }
                Interlocked.Exchange(ref _lastConnectAttemptTicks, 0);
                lock (_lock) _loggedResizeOutcomes.Clear();
                Volatile.Write(ref _detectionStartedSeen, 0);
                MarkDetectionActivity();
                Interlocked.Exchange(ref _redetectRequestedTicks, 0);
                Interlocked.Exchange(ref _daemonFailures, 0);
                // The process generation ends here, before a replacement SDK session
                // can publish its generation. Retire every older continuation and
                // transfer only this admitted bounce to the successor scope.
                lock (_lock)
                {
                    _metadataRetry.Reset();
                    owner = CaptureRefreshOwner();
                }
                ServiceLog.Info($"[rgb-bridge] stopping daemon generation ({reason})");
                (RedetectionProcessStopOverride ?? _proc.Stop)();
                stopped = true;
            }, requireConnected: false);
            // The old publication intentionally retired itself; validate its successor.
            if (!stopped || !OwnsRefreshScope(owner)) return;

            try
            {
                if (_controller is IOpenRgbRefreshTransport transport)
                {
                    if (!await transport.TryDisconnectAsync(owner.Generation, () => OwnsRefreshScope(owner), owner.Cancellation)
                        .ConfigureAwait(false)) return;
                }
                else
                {
                    RequireRedetectionOwner(owner);
                    await _controller.DisconnectAsync().ConfigureAwait(false);
                }
            }
            catch (Exception) when (!OwnsRefreshScope(owner)) { return; }
            catch (Exception ex)
            {
                // Stop already retired this daemon. Preserve explicit recovery's
                // best-effort disconnect, without granting an obsolete chain a start.
                PublishRefresh(owner, () => ServiceLog.Warn($"[rgb-bridge] bounce ({reason}): disconnect failed: {ex.Message}; continuing current recovery"),
                    requireConnected: false);
            }
            RequireRedetectionOwner(owner);
            await Task.Delay(500, owner.Cancellation).ConfigureAwait(false);
            var started = false;
            if (!PublishRefresh(owner, () =>
            {
                if (OsShuttingDown()) return;
                (RedetectionProcessStartOverride ?? _proc.Start)();
                started = true;
                lock (_lock)
                {
                    _deferredRedetectReason = null;
                    _deferredDetectorsChanged = false;
                    _deferredRedetectOwner = null;
                }
            }, requireConnected: false) || !started) return;
            RequireRedetectionOwner(owner);
            await EnsureConnectedOwnedAsync(owner).ConfigureAwait(false);
        }
        catch (Exception) when (!OwnsRefreshScope(owner)) { }
        catch (Exception ex)
        {
            PublishRefresh(owner, () => ServiceLog.Warn($"[rgb-bridge] bounce ({reason}) failed: {ex.GetType().Name}: {ex.Message}"),
                requireConnected: false);
        }
    }
}
