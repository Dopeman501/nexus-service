using Options = Nexus.Service.Diagnostics.EfficiencyDiagnosticOptions;
using Owner = Nexus.Service.Diagnostics.DiagnosticLeaseOwner<object>;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace Nexus.Service.Tests.EfficiencyLease;

public sealed class OwnerTests
{
    private const string Run = "22222222222222222222222222222222";
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Activation_after_construction_is_lazy_and_each_run_has_a_distinct_owner(bool overlay)
    {
        var authority=Guid.NewGuid().ToString("N"); var owner=new Owner();var created=0;
        var other=new Nexus.Overlay.DiagnosticLeaseOwner<object>();
        Func<Func<object>,object?> get=overlay?other.Get:owner.Get;
        Func<string,long,string?,int,bool> apply=overlay?Nexus.Overlay.EfficiencyDiagnosticOptions.Apply:Options.Apply;
        apply(authority,0,null,0); Assert.Null(get(()=>{created++;return new();}));
        apply(authority,1,Run,30000);var first=get(()=>{created++;return new();});
        Assert.Same(first,get(()=>throw new Exception()));Assert.Equal(1,created);
        apply(authority,2,null,0);Assert.Null(get(()=>throw new Exception()));
        apply(authority,3,"33333333333333333333333333333333",30000);
        Assert.NotSame(first,get(()=>{created++;return new();}));Assert.Equal(2,created);
        apply(authority,4,null,0);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Cancellation_during_factory_cannot_admit_an_obsolete_owner(bool overlay)
    {
        var authority=Guid.NewGuid().ToString("N");var owner=new Owner();var other=new Nexus.Overlay.DiagnosticLeaseOwner<object>();
        Func<Func<object>,object?> get=overlay?other.Get:owner.Get;
        Func<string,long,string?,int,bool> apply=overlay?Nexus.Overlay.EfficiencyDiagnosticOptions.Apply:Options.Apply;
        apply(authority,1,Run,30000);
        var value=get(()=>{apply(authority,2,null,0);return new object();});
        Assert.Null(value);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Explicit_deactivation_and_invalid_projection_never_reenable_legacy(bool overlay)
    {
        var authority=Guid.NewGuid().ToString("N");
        Func<string,long,string?,int,bool> apply=overlay?Nexus.Overlay.EfficiencyDiagnosticOptions.Apply:Options.Apply;
        Assert.False(apply("bad",0,Run,30000));
        Assert.True(apply(authority,1,Run,30000));
        if(overlay){Assert.True(Nexus.Overlay.EfficiencyDiagnosticOptions.Enabled);Nexus.Overlay.EfficiencyDiagnosticOptions.Deactivate();Assert.False(Nexus.Overlay.EfficiencyDiagnosticOptions.Enabled);}
        else{Assert.True(Options.Enabled);Options.Deactivate();Assert.False(Options.Enabled);}
        foreach(var value in new string?[]{null,"","0","true"," 1","1 "})
        {Assert.False(Options.IsEnabled(value));Assert.False(Nexus.Overlay.EfficiencyDiagnosticOptions.IsEnabled(value));}
        Assert.True(Options.IsEnabled("1"));Assert.True(Nexus.Overlay.EfficiencyDiagnosticOptions.IsEnabled("1"));
    }
}
