using System.Collections.Generic;
using Nexus.Service.Peripherals.JungleLeopard;

namespace Nexus.Service.Devices.Handlers;

internal sealed class JungleLeopardD215Handler : IDeviceHandler
{
    internal const string HandlerId = "jungle-leopard-d215";

    private readonly ID215PortDiscovery _discovery;
    private readonly D215ConnectionWorker? _worker;

    public JungleLeopardD215Handler(ID215PortDiscovery discovery, D215ConnectionWorker? worker = null)
    {
        _discovery = discovery;
        _worker = worker;
    }

    public string Id => HandlerId;
    public string Name => "Jungle Leopard D215";
    public string Category => "display";
    public IReadOnlyList<UsbId> Identifiers { get; } = [new UsbId(D215PortDiscovery.VendorId, D215PortDiscovery.ProductId)];

    public bool IsConnected(IReadOnlyList<UsbDeviceEntry> detectedDevices)
    {
        if (_worker is null) return _discovery.Discover().Count > 0;
        var snapshot = _worker.Snapshot;
        return snapshot.Port is not null || snapshot.Status == D215ConnectionStatus.Ambiguous;
    }

    public string GetFirmwareVersion() => _worker?.Snapshot.Info?.Firmware ?? "";

    public string? GetWarning(IReadOnlyList<UsbDeviceEntry> detectedDevices) => _worker?.Snapshot.Warning;
}
