using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Lighting.Rgb;

public sealed partial class RgbBridge
{
    // Lock order: controller connection publication -> bridge publication -> _lock.
    // Lifecycle invalidation takes only publication -> _lock, never calls controller I/O there.
    private readonly object _refreshPublicationGate = new();
    private readonly record struct RefreshOwner(long Scope, int Generation, CancellationToken Cancellation);
    private sealed class RefreshRetiredException : OperationCanceledException { }

    private RefreshOwner CaptureRefreshOwner()
    {
        lock (_lock) return new(_metadataRetry.Scope, MetadataConnectionGeneration, _refreshCts?.Token ?? default);
    }

    private bool OwnsRefreshScope(RefreshOwner owner)
    {
        lock (_lock)
            return _active && !_disposed && !owner.Cancellation.IsCancellationRequested
                && owner.Scope == _metadataRetry.Scope && owner.Generation == MetadataConnectionGeneration;
    }

    private bool OwnsRefreshLifecycle(RefreshOwner owner)
    {
        lock (_lock)
            return _active && !_disposed && !owner.Cancellation.IsCancellationRequested && owner.Scope == _metadataRetry.Scope;
    }

    private bool IsRefreshCurrent(RefreshOwner owner) => OwnsRefreshScope(owner) && _controller.IsConnected;

    private void RequireRefreshCurrent(RefreshOwner owner)
    {
        if (!IsRefreshCurrent(owner)) throw new RefreshRetiredException();
    }

    private bool PublishRefresh(RefreshOwner owner, Action publication, bool requireConnected = true)
    {
        var published = false;
        bool Current() => OwnsRefreshScope(owner) && (!requireConnected || _controller.IsConnected);
        void Commit()
        {
            lock (_refreshPublicationGate)
            {
                if (!Current()) return;
                publication();
                published = Current(); // A synchronous subscriber may retire us reentrantly.
            }
        }
        if (_controller is IOpenRgbRefreshTransport transport)
            return transport.TryPublishConnection(owner.Generation, Commit, requireConnected) && published;
        Commit(); // Non-SDK contributors/test controllers retain their existing connection contract.
        return published;
    }

    private void PublishRefreshFailure(RefreshOwner owner, Action failure)
    {
        void Commit()
        {
            lock (_refreshPublicationGate)
            {
                if (OwnsRefreshScope(owner)) failure();
            }
        }
        if (_controller is IOpenRgbRefreshTransport transport)
            transport.TryPublishConnection(owner.Generation, Commit, requireConnected: false);
        else Commit();
    }

    private async Task SetRefreshDirectModeAsync(RefreshOwner owner, RgbDevice device)
    {
        RequireRefreshCurrent(owner);
        if (_controller is IOpenRgbRefreshTransport transport)
        {
            if (!await transport.TrySetDirectModeAsync(device, owner.Generation, () => IsRefreshCurrent(owner), owner.Cancellation).ConfigureAwait(false))
                throw new RefreshRetiredException();
        }
        else await _controller.SetDirectModeAsync(device, owner.Cancellation).ConfigureAwait(false);
        RequireRefreshCurrent(owner);
    }

    private async Task ResizeForRefreshAsync(RefreshOwner owner, int address, int zone, int size, Action? admitted = null)
    {
        RequireRefreshCurrent(owner);
        if (_controller is IOpenRgbRefreshTransport transport)
        {
            if (!await transport.TryResizeZoneAsync(address, zone, size, owner.Generation,
                () => PublishRefresh(owner, () => admitted?.Invoke()), owner.Cancellation).ConfigureAwait(false))
                throw new RefreshRetiredException();
        }
        else
        {
            if (!PublishRefresh(owner, () => admitted?.Invoke())) throw new RefreshRetiredException();
            await _controller.ResizeZoneAsync(address, zone, size, owner.Cancellation).ConfigureAwait(false);
        }
        RequireRefreshCurrent(owner);
    }

    // Queued user intent keeps stable identity until command admission. A retired waiter
    // cannot drain a newer queue or resend a command whose completion was uncertain.
    private readonly Dictionary<(string Id, int Zone), PendingIdResize> _stableZoneResizes = new();

    private void StageQueuedResizes(RefreshOwner owner, IReadOnlyList<RgbDevice> devices)
    {
        if (!PublishRefresh(owner, () =>
        {
            while (_pendingIdZoneResizes.TryDequeue(out var request)) Stage(request);
            while (_pendingZoneResizes.TryDequeue(out var request))
            {
                if (FindDevice(devices, request.physIdx) is { } device)
                    Stage(new(device.StableId, request.zoneIdx, request.newSize, DateTime.UtcNow.Ticks));
            }
        })) throw new RefreshRetiredException();

        void Stage(PendingIdResize request)
        {
            var key = (request.DeviceId, request.ZoneIdx);
            if (!_stableZoneResizes.TryGetValue(key, out var existing) || request.EnqueuedTicks >= existing.EnqueuedTicks)
                _stableZoneResizes[key] = request;
        }
    }

    private bool TrySelectQueuedResize(RefreshOwner owner, IReadOnlyList<RgbDevice> devices,
        out PendingIdResize request, out int address)
    {
        PendingIdResize selected = default;
        var selectedAddress = -1;
        if (!PublishRefresh(owner, () =>
        {
            var settings = _store.Load();
            foreach (var candidate in _stableZoneResizes.Values.ToArray())
            {
                var device = devices.FirstOrDefault(d => d.StableId == candidate.DeviceId);
                if (device is not null && IsSizableDevice(device, settings))
                {
                    selected = candidate;
                    selectedAddress = device.Address;
                    break;
                }
                if (DateTime.UtcNow.Ticks - candidate.EnqueuedTicks > PendingResizeLifetime.Ticks || device is not null)
                    _stableZoneResizes.Remove((candidate.DeviceId, candidate.ZoneIdx));
            }
        })) throw new RefreshRetiredException();
        request = selected;
        address = selectedAddress;
        return selectedAddress >= 0;
    }

    private void AdmitQueuedResize(PendingIdResize request)
    {
        var key = (request.DeviceId, request.ZoneIdx);
        if (_stableZoneResizes.TryGetValue(key, out var pending) && pending == request) _stableZoneResizes.Remove(key);
    }
}
