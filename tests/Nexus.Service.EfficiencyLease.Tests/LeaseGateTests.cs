using ServiceGate = Nexus.Service.Diagnostics.DiagnosticLeaseGate;
using OverlayGate = Nexus.Overlay.DiagnosticLeaseGate;

namespace Nexus.Service.Tests.EfficiencyLease;

public sealed class LeaseGateTests
{
    private const string Authority = "11111111111111111111111111111111";
    private const string Run = "22222222222222222222222222222222";

    [Fact]
    public void Initially_off_and_deadline_expires_without_a_timer()
    {
        long now = 100;
        var gate = new ServiceGate(() => now);
        Assert.Equal(0, gate.SessionRevision);
        Assert.True(gate.Apply(Authority, 1, Run, 30_000));
        var epoch = gate.SessionRevision;
        Assert.True(epoch > 0);
        now += 29_999;
        Assert.Equal(epoch, gate.SessionRevision);
        now++;
        Assert.Equal(0, gate.SessionRevision);
    }

    [Fact]
    public void Repeated_projection_cannot_extend_or_revive_a_run()
    {
        long now = 0;
        var gate = new OverlayGate(() => now);
        Assert.True(gate.Apply(Authority, 1, Run, 30_000));
        now = 20_000;
        Assert.True(gate.Apply(Authority, 1, Run, 30_000));
        now = 30_000;
        Assert.Equal(0, gate.SessionRevision);
        Assert.False(gate.Apply(Authority, 1, Run, 30_000));
        Assert.Equal(0, gate.SessionRevision);
    }

    [Fact]
    public void Cancellation_stale_revision_and_new_epoch_are_distinct()
    {
        long now = 10;
        var gate = new ServiceGate(() => now);
        gate.Apply(Authority, 1, Run, 40_000);
        var first = gate.SessionRevision;
        Assert.True(gate.Apply(Authority, 2, null, 0));
        Assert.Equal(0, gate.SessionRevision);
        Assert.False(gate.Apply(Authority, 1, Run, 40_000));
        Assert.True(gate.Apply(Authority, 3, "33333333333333333333333333333333", 40_000));
        Assert.True(gate.SessionRevision > first);
    }

    [Theory]
    [InlineData("bad", 1, Run, 30000)]
    [InlineData(Authority, -1, Run, 30000)]
    [InlineData(Authority, 1, "bad", 30000)]
    [InlineData(Authority, 1, Run, -1)]
    [InlineData(Authority, 1, Run, 2100001)]
    [InlineData(Authority, 1, null, 30)]
    public void Invalid_projection_is_rejected(string authority, long revision, string? run, int remaining)
    {
        var gate = new ServiceGate(() => 1);
        Assert.False(gate.Apply(authority, revision, run, remaining));
        Assert.Equal(0, gate.SessionRevision);
        var client=new OverlayGate(()=>1);Assert.False(client.Apply(authority,revision,run,remaining));Assert.Equal(0,client.SessionRevision);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void Higher_revision_cannot_renew_same_run_and_retired_authority_is_rejected(bool overlay)
    {
        long now=0;
        var service=new ServiceGate(()=>now);var client=new OverlayGate(()=>now);
        Func<string,long,string?,int,bool> apply=overlay?client.Apply:service.Apply;
        Func<long> epoch=()=>overlay?client.SessionRevision:service.SessionRevision;
        Assert.True(apply(Authority,1,Run,30000));var first=epoch();
        now=20000;Assert.True(apply(Authority,2,Run,30000));Assert.Equal(first,epoch());
        now=30000;Assert.Equal(0,epoch());Assert.False(apply(Authority,3,Run,30000));
        var other="55555555555555555555555555555555";
        Assert.True(apply(other,0,null,0));Assert.False(apply(Authority,4,Run,30000));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Authority_history_is_bounded_and_empty_projection_is_idempotent(bool overlay)
    {
        long now=0;var service=new ServiceGate(()=>now);var client=new OverlayGate(()=>now);
        Func<string,long,string?,int,bool> apply=overlay?client.Apply:service.Apply;
        for(var n=1;n<=12;n++)Assert.True(apply(n.ToString("x32"),0,null,0));
        Assert.True(apply(12.ToString("x32"),0,null,0));
        Assert.False(apply("gggggggggggggggggggggggggggggggg",0,null,0));
        if(overlay){client.Deactivate();Assert.Equal(0,client.SessionRevision);}else{service.Deactivate();Assert.Equal(0,service.SessionRevision);}
    }
}
