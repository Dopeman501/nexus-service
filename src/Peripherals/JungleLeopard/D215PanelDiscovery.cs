using System.Collections.Generic;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Panel.Streams;

namespace Nexus.Service.Peripherals.JungleLeopard;

/// <summary>Projects the verified serial lease onto the standard streamed-panel coordinator.</summary>
internal sealed class D215PanelDiscovery(
    D215ConnectionWorker worker, StreamedPanelStore store, PanelDeviceRegistry registry) : IStreamedPanelDiscovery
{
    private static readonly StreamedPanelProfile PortraitProfile = new()
    {
        Kind = D215ConnectionWorker.HandlerId,
        Family = D215ConnectionWorker.HandlerId,
        DisplayName = "Jungle Leopard D215",
        Surface = PanelSurfaces.LcdPortrait,
        CssWidth = D215BgraFrames.Width,
        CssHeight = D215BgraFrames.Height,
        Dpi = 276,
        Dpr = 1,
        Fps = D215Timing.FramesPerSecond,
        WriteBatchFrames = 1,
        Codec = StreamCodec.RawBgra,
        RequireNativeRawFrames = true,
        KeepLatestRawFrame = true,
        MultiInstance = true,
        SupportsPortrait = true,
    };

    private static readonly StreamedPanelProfile LandscapeProfile = PortraitProfile with
    {
        Surface = PanelSurfaces.LcdWide,
        CssWidth = D215BgraFrames.Height,
        CssHeight = D215BgraFrames.Width,
    };

    public string HandlerId => D215ConnectionWorker.HandlerId;

    public IReadOnlyList<StreamedPanelDeviceInfo> Discover()
    {
        var identity = worker.Snapshot.Port?.Identity;
        if (identity is null || worker.GetIdentifiedSession(identity) is null) return [];
        // Records from before the orientation setting default to the original portrait geometry.
        var landscape = store.Load().TryGetValue(identity, out var mapping)
            && registry.Get(mapping.PanelDeviceId) is { Portrait: false, Capabilities.Family: D215ConnectionWorker.HandlerId };
        return [new StreamedPanelDeviceInfo { Serial = identity, Profile = landscape ? LandscapeProfile : PortraitProfile }];
    }

    public IStreamedPanelTransport CreateTransport(StreamedPanelDeviceInfo info) =>
        new D215StreamTransport(info.Serial, worker, rotateLandscape: info.Profile.Surface == PanelSurfaces.LcdWide);
}
