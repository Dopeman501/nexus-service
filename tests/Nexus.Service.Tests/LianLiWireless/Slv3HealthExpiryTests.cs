#if SLV3_FAKE_NATIVE
using Nexus.Service.Peripherals.LianLiWireless;

namespace Nexus.Service.Tests.LianLiWireless;

public sealed class Slv3HealthExpiryTests
{
    [Fact]
    public void Production_diagnostics_follow_live_epochs_while_explicit_null_overrides_stay_off()
    {
        var authority = Guid.NewGuid().ToString("N");
        using var hub = new Slv3Hub(new Slv3TestHub.FakeDiscovery(), _ => throw new InvalidOperationException());
        using var explicitOff = new Slv3Hub(new Slv3TestHub.FakeDiscovery(), _ => throw new InvalidOperationException())
        { GetDevDiagnostics = null };
        try
        {
            Assert.True(Nexus.Service.Diagnostics.EfficiencyDiagnosticOptions.Apply(authority, 1, Guid.NewGuid().ToString("N"), 10_000));
            var first = Assert.IsType<Slv3GetDevDiagnostics>(hub.GetDevDiagnostics);
            Assert.Same(first, hub.GetDevDiagnostics);
            Assert.Null(explicitOff.GetDevDiagnostics);
            Assert.True(Nexus.Service.Diagnostics.EfficiencyDiagnosticOptions.Apply(authority, 2, null, 0));
            Assert.Null(hub.GetDevDiagnostics);
            Assert.True(Nexus.Service.Diagnostics.EfficiencyDiagnosticOptions.Apply(authority, 3, Guid.NewGuid().ToString("N"), 10_000));
            Assert.NotSame(first, hub.GetDevDiagnostics);
        }
        finally { Nexus.Service.Diagnostics.EfficiencyDiagnosticOptions.Apply(authority, 4, null, 0); }
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void Missing_declared_records_preserve_the_last_chain_instead_of_expiring_it(bool diagnostics)
    {
        using var f = new Fixture(diagnostics);
        f.Rx.Reply = _ => new byte[] { 0x10, 1, 0, 0 };
        f.Now = 30_001;
        Assert.True(f.Hub.PollTick());
        Assert.Equal(f.Mac, Assert.Single(f.Hub.State.Fans).Mac);
        f.Now = 90_001;
        Assert.True(f.Hub.PollTick());
        Assert.True(Assert.Single(f.Hub.State.Fans).Stale);
        f.Rx.Reply = _ => new byte[64];
        f.Now = 91_001;
        Assert.True(f.Hub.PollTick());
        Assert.Equal(f.Mac, Assert.Single(f.Hub.State.Fans).Mac);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void Invalid_declared_record_preserves_the_last_chain(bool diagnostics)
    {
        using var f = new Fixture(diagnostics);
        f.Rx.Reply = _ => new byte[46];
        var invalid = f.Rx.Reply(46); invalid[0] = 0x10; invalid[1] = 1;
        f.Rx.Reply = _ => invalid;
        f.Now = 30_001;
        Assert.True(f.Hub.PollTick());
        Assert.Equal(f.Mac, Assert.Single(f.Hub.State.Fans).Mac);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void An_echo_only_header_cannot_attest_recovery_but_a_complete_list_can(bool diagnostics)
    {
        using var f = new Fixture(diagnostics);
        f.Rx.Reply = _ => new byte[64];
        for (var i = 0; i < 60; i++) Assert.True(f.Hub.PollTick());
        Assert.Equal(Slv3LinkStatus.Recovering, f.Hub.State.LinkStatus);
        f.Rx.Reply = _ => new byte[] { 0x10, 1, 0, 0 };
        Assert.True(f.Hub.PollTick());
        Assert.Equal(Slv3LinkStatus.Recovering, f.Hub.State.LinkStatus);
        f.Rx.Reply = null;
        Assert.True(f.Hub.PollTick());
        Assert.Equal(Slv3LinkStatus.Ok, f.Hub.State.LinkStatus);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void A_complete_zero_record_list_keeps_answered_omission_expiry(bool diagnostics)
    {
        using var f = new Fixture(diagnostics);
        f.Rx.Reply = _ => new byte[] { 0x10, 0, 0, 0 };
        f.Now = 30_001;
        Assert.True(f.Hub.PollTick());
        Assert.Empty(f.Hub.State.Fans);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void Valid_partial_records_update_presence_and_learn_more_pages_without_expiring_omissions(bool diagnostics)
    {
        using var f = new Fixture(diagnostics);
        f.Net.Fans[0].FanCount = 3;
        var partial = f.Rx.Normal(434); partial[1] = 12;
        f.Rx.Reply = _ => partial;
        f.Now = 31_000;
        Assert.True(f.Hub.PollTick());
        Assert.Equal(3, Assert.Single(f.Hub.State.Fans).FanCount);
        Assert.True(f.Hub.PollTick());
        Assert.Equal(2, f.Rx.Pages.Last());
        f.Rx.Reply = _ => new byte[] { 0x10, 12, 0, 0 };
        f.Now = 91_000;
        Assert.True(f.Hub.PollTick());
        Assert.Equal(3, Assert.Single(f.Hub.State.Fans).FanCount);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void Positive_partial_sighting_does_not_inherit_blind_time_before_its_observation(bool diagnostics)
    {
        using var f = new Fixture(diagnostics);
        f.Rx.Reply = _ => new byte[] { 0x10, 1, 0, 0 };
        f.Now = 1_000; Assert.True(f.Hub.PollTick());
        var partial = f.Rx.Normal(434); partial[1] = 2;
        f.Rx.Reply = _ => partial;
        f.Now = 61_000; Assert.True(f.Hub.PollTick());
        f.Rx.Reply = _ => new byte[] { 0x10, 0, 0, 0 };
        f.Now = 62_000; Assert.True(f.Hub.PollTick());
        Assert.Single(f.Hub.State.Fans);
        f.Now = 91_000; Assert.True(f.Hub.PollTick());
        Assert.Single(f.Hub.State.Fans);
        f.Now = 92_001; Assert.True(f.Hub.PollTick());
        Assert.Empty(f.Hub.State.Fans);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void A_header_without_records_does_not_clear_an_existing_failure_streak(bool diagnostics)
    {
        using var f = new Fixture(diagnostics);
        f.Rx.Reply = _ => Array.Empty<byte>();
        for (var i = 0; i < 4; i++) Assert.True(f.Hub.PollTick());
        f.Rx.Reply = _ => new byte[] { 0x10, 1, 0, 0 };
        Assert.True(f.Hub.PollTick());
        f.Rx.Reply = _ => Array.Empty<byte>();
        Assert.True(f.Hub.PollTick());
        Assert.Equal(Slv3LinkStatus.Recovering, f.Hub.State.LinkStatus);
    }

    private sealed class Fixture : IDisposable
    {
        public long Now;
        public readonly Slv3TestHub.FakeSlv3Network Net = new();
        public readonly Slv3TestHub.FakeTxTransport Tx;
        public readonly ReplyRx Rx;
        public readonly Slv3Hub Hub;
        public string Mac => Convert.ToHexString(Net.Fans[0].Mac);

        public Fixture(bool diagnostics)
        {
            Tx = new(Net); Rx = new(new(Net));
            Hub = new(new Slv3TestHub.FakeDiscovery(), p => p.Role == Slv3DongleRole.Tx ? Tx : Rx, () => Now)
            { ConfirmNewChains = false, SleepMs = _ => { }, GetDevDiagnostics = diagnostics ? new() : null };
            Assert.True(Hub.EnsureConnected());
            Net.Fans.Add(new() { Mac = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 }, MasterMac = Net.MasterMac, RxType = 1, FansType = 24 });
            Assert.True(Hub.PollTick());
        }
        public void Dispose() => Hub.Dispose();
    }

    private sealed class ReplyRx(Slv3TestHub.FakeRxTransport normal) : ISlv3Transport
    {
        public Func<int, byte[]>? Reply;
        public readonly List<byte> Pages = new();
        public byte[] Normal(int length) => normal.RfRead(length);
        public bool IsOpen => true;
        public Slv3DongleRole Role => Slv3DongleRole.Rx;
        public string PortName => "fake-rx";
        public bool RfSend(ReadOnlySpan<byte> frame) { Pages.Add(frame[1]); return true; }
        public byte[] RfRead(int length) => Reply?.Invoke(length) ?? Normal(length);
        public void Dispose() { }
    }
}
#endif
