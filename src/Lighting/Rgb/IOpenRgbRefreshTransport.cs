using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>Private refresh commands/publication belong to one SDK connection, never reused addresses.</summary>
internal interface IOpenRgbRefreshTransport
{
    bool TryPublishConnection(int generation, Action publication, bool requireConnected = true);
    Task<bool> TrySetDirectModeAsync(RgbDevice device, int generation, Func<bool> current, CancellationToken ct);
    Task<bool> TryResizeZoneAsync(int address, int zone, int size, int generation, Func<bool> current, CancellationToken ct);
    Task<bool?> TrySetSettingsAsync(string key, string json, int generation, Func<bool> current, CancellationToken ct);
    Task<bool?> TryRescanAsync(int generation, Func<bool> current, CancellationToken ct);
    Task<bool> TryDisconnectAsync(int generation, Func<bool> current, CancellationToken ct);
}
