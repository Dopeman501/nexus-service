using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.LianLiWireless;

namespace Nexus.Service.Tests.LianLiWireless;

/// <summary>Actual Hub→provider behavior with fake discovery and transport only.</summary>
public sealed class Slv3HealthProviderTests
{
    [Theory][InlineData(1, 13)][InlineData(3, 39)]
    public void Incomplete_lists_preserve_both_ring_cards_and_their_existing_frame_instances(int fans, int leds)
    {
        using var f = new Fixture(fans);
        var cards = f.Provider.GetAll().Devices.Select(x => x.Id).ToArray();
        var frames = f.Provider.BuildFrames(7).ToArray();
        Assert.Equal(2, frames.Length);
        Assert.All(frames, x => Assert.Equal(leds, x.LedCount));
        f.Rx.Reply = _ => new byte[] { 0x10, 1, 0, 0 };
        f.Now = 31_000;
        Assert.True(f.Hub.PollTick());
        f.Provider.OnHubStateUpdated();
        Assert.Equal(cards, f.Provider.GetAll().Devices.Select(x => x.Id));
        var after = f.Provider.BuildFrames(7);
        Assert.Equal(frames.Length, after.Count);
        for (var i = 0; i < frames.Length; i++) Assert.Same(frames[i], after[i]);
    }

    [Theory][InlineData(1)][InlineData(3)]
    public void Reported_input_mode_keeps_cards_but_retires_engine_frames_and_can_return(int fans)
    {
        using var f = new Fixture(fans);
        var ids = f.Provider.GetAll().Devices.Select(x => x.Id).ToArray();
        Assert.Equal(2, f.Provider.BuildFrames(0).Count);
        f.Net.Fans[0].PlayingMotherboardArgb = true;
        Assert.True(f.Hub.PollTick());
        f.Provider.OnHubStateUpdated();
        var cards = f.Provider.GetAll().Devices;
        Assert.Equal(ids, cards.Select(x => x.Id));
        Assert.All(cards, x => Assert.Equal("argbInput", x.DevicePageReason));
        Assert.Empty(f.Provider.BuildFrames(0));
        f.Net.Fans[0].PlayingMotherboardArgb = false;
        Assert.True(f.Hub.PollTick());
        f.Provider.OnHubStateUpdated();
        Assert.Equal(ids, f.Provider.GetAll().Devices.Select(x => x.Id));
        Assert.Equal(2, f.Provider.BuildFrames(0).Count);
    }

    [Theory][InlineData(1)][InlineData(3)]
    public void Valid_zero_fan_count_and_binding_changes_keep_their_previous_provider_semantics(int fans)
    {
        using var f = new Fixture(fans);
        var ids = f.Provider.GetAll().Devices.Select(x => x.Id).ToArray();
        f.Net.Fans[0].FanCount = 0;
        Assert.True(f.Hub.PollTick());
        Assert.Empty(f.Provider.GetAll().Devices);
        Assert.Empty(f.Provider.BuildFrames(0));
        f.Net.Fans[0].FanCount = (byte)fans;
        Assert.True(f.Hub.PollTick());
        Assert.Equal(ids, f.Provider.GetAll().Devices.Select(x => x.Id));
        f.Net.Fans[0].MasterMac = new byte[6];
        Assert.True(f.Hub.PollTick());
        Assert.Empty(f.Provider.GetAll().Devices);
        Assert.Empty(f.Provider.BuildFrames(0));
    }

    private sealed class Fixture : IDisposable
    {
        public long Now;
        public readonly Slv3TestHub.FakeSlv3Network Net = new();
        public readonly ReplyRx Rx;
        public readonly Slv3Hub Hub;
        public readonly Slv3LightingDeviceProvider Provider;
        public Fixture(int fans)
        {
            var tx = new Slv3TestHub.FakeTxTransport(Net); Rx = new(new(Net));
            Hub = new(new Slv3TestHub.FakeDiscovery(), p => p.Role == Slv3DongleRole.Tx ? tx : Rx, () => Now)
            { ConfirmNewChains = false, SleepMs = _ => { }, GetDevDiagnostics = null };
            Assert.True(Hub.EnsureConnected());
            Net.Fans.Add(new() { Mac = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 }, MasterMac = Net.MasterMac, RxType = 1, FansType = 28, FanCount = (byte)fans });
            Assert.True(Hub.PollTick());
            Provider = new(Hub, new InMemoryConfigStore(), new Np50IdentifyTracker());
        }
        public void Dispose() => Hub.Dispose();
    }

    private sealed class ReplyRx(Slv3TestHub.FakeRxTransport normal) : ISlv3Transport
    {
        public Func<int, byte[]>? Reply;
        public bool IsOpen => true;
        public Slv3DongleRole Role => Slv3DongleRole.Rx;
        public string PortName => "fake-rx";
        public bool RfSend(ReadOnlySpan<byte> frame) => true;
        public byte[] RfRead(int length) => Reply?.Invoke(length) ?? normal.RfRead(length);
        public void Dispose() { }
    }
}
