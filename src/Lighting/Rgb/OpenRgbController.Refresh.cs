using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting.Rgb;

public sealed partial class OpenRgbController
{
    // Short synchronous publication is serialized with session replacement/retirement.
    // No controller await is performed while this gate is held.
    private readonly object _connectionPublicationGate = new();

    bool IOpenRgbRefreshTransport.TryPublishConnection(int generation, Action publication, bool requireConnected)
    {
        lock (_connectionPublicationGate)
        {
            if (generation != ConnectionGeneration || (requireConnected && !IsConnected)) return false;
            publication();
            return generation == ConnectionGeneration && (!requireConnected || IsConnected);
        }
    }

    async Task<bool> IOpenRgbRefreshTransport.TrySetDirectModeAsync(RgbDevice device, int generation,
        Func<bool> current, CancellationToken ct)
    {
        await WaitRequestLockAsync(ct).ConfigureAwait(false);
        try
        {
            var session = Volatile.Read(ref _session);
            if (session is not { Closed: false, Ready: true } || generation != ConnectionGeneration || !current()) return false;
            var mode = device.FindCustomMode();
            await SendOnSessionAsync(session, (uint)device.Address,
                mode is null ? OpenRgbProtocol.PacketId.SetCustomMode : OpenRgbProtocol.PacketId.RgbControllerUpdateMode,
                mode is null ? null : OpenRgbProtocol.BuildUpdateModeBody(mode.Index, mode.Bytes), ct).ConfigureAwait(false);
            return true;
        }
        finally { _requestLock.Release(); }
    }

    async Task<bool> IOpenRgbRefreshTransport.TryResizeZoneAsync(int address, int zone, int size, int generation,
        Func<bool> current, CancellationToken ct)
    {
        if (zone < 0 || size < 0) return false;
        await WaitRequestLockAsync(ct).ConfigureAwait(false);
        try
        {
            var session = Volatile.Read(ref _session);
            if (session is not { Closed: false, Ready: true } || generation != ConnectionGeneration || !current()) return false;
            var (_, status) = await RequestLockedAsync(session, OpenRgbProtocol.PacketId.RgbControllerResizeZone, (uint)address,
                OpenRgbProtocol.BuildResizeZoneBody(zone, (uint)size), reply: null, replyOnly: false, ct).ConfigureAwait(false);
            if (status != OpenRgbProtocol.StatusOk)
                ServiceLog.Warn($"[openrgb] resize controller {address} zone {zone} -> {size} refused (status {status})");
            return true; // Admitted, including an acknowledged rejection; do not retry uncertain writes.
        }
        finally { _requestLock.Release(); }
    }

    Task<bool?> IOpenRgbRefreshTransport.TrySetSettingsAsync(string key, string json, int generation,
        Func<bool> current, CancellationToken ct) => RequestForRefreshAsync(
            OpenRgbProtocol.PacketId.SettingsManagerSetSettings,
            Encoding.UTF8.GetBytes("{\"" + System.Text.Json.JsonEncodedText.Encode(key) + "\":" + json + "}"),
            generation, current, requireLocal: true, ct);

    Task<bool?> IOpenRgbRefreshTransport.TryRescanAsync(int generation, Func<bool> current, CancellationToken ct) =>
        RequestForRefreshAsync(OpenRgbProtocol.PacketId.RequestRescanDevices, null, generation, current, requireLocal: false, ct);

    // null means retired before admission; false is a current daemon refusal/failure.
    private async Task<bool?> RequestForRefreshAsync(OpenRgbProtocol.PacketId packet, byte[]? body, int generation,
        Func<bool> current, bool requireLocal, CancellationToken ct)
    {
        await WaitRequestLockAsync(ct).ConfigureAwait(false);
        try
        {
            var session = Volatile.Read(ref _session);
            if (generation != ConnectionGeneration || !current()) return null;
            if (session is not { Closed: false, Ready: true } || (requireLocal && !session.LocalClient)) return false;
            var (_, status) = await RequestLockedAsync(session, packet, 0, body, reply: null, replyOnly: false, ct).ConfigureAwait(false);
            return status == OpenRgbProtocol.StatusOk;
        }
        catch (IOException) { return false; }
        finally { _requestLock.Release(); }
    }

    async Task<bool> IOpenRgbRefreshTransport.TryDisconnectAsync(int generation, Func<bool> current, CancellationToken ct)
    {
        await WaitRequestLockAsync(ct).ConfigureAwait(false);
        try
        {
            if (generation != ConnectionGeneration || !current()) return false;
            CloseSession(Volatile.Read(ref _session));
            return true;
        }
        finally { _requestLock.Release(); }
    }
}
