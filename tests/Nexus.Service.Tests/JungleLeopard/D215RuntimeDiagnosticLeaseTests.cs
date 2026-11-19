using Nexus.Service.Diagnostics;
using Nexus.Service.Peripherals.JungleLeopard;

namespace Nexus.Service.Tests.JungleLeopard;

[CollectionDefinition("Runtime diagnostic lease", DisableParallelization=true)]
public sealed class RuntimeDiagnosticLeaseCollection { }

[Collection("Runtime diagnostic lease")]
public sealed class D215RuntimeDiagnosticLeaseTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Mid_encode_activation_or_replacement_cannot_charge_the_new_run(bool initiallyActive)
    {
        var authority=Guid.NewGuid().ToString("N");
        EfficiencyDiagnosticOptions.Apply(authority,0,null,0);
        if(initiallyActive)EfficiencyDiagnosticOptions.Apply(authority,1,"11111111111111111111111111111111",30000);
        using var frames=new D215BgraFrames(2,_=>new Encoder(()=>
        {
            EfficiencyDiagnosticOptions.Apply(authority,2,null,0);
            EfficiencyDiagnosticOptions.Apply(authority,3,"33333333333333333333333333333333",30000);
        }));
        try
        {
            var old=frames.Diagnostics;frames.Queue([1,2]);using var frame=frames.TakeLatestEnvelope();
            Assert.False(frame.IsEmpty);
            var current=Assert.IsType<D215FrameDiagnostics>(frames.Diagnostics);
            Assert.NotSame(old,current);
            Assert.Equal(0,current.Snapshot().FreshEncoded);
            Assert.Equal(0,current.Snapshot().Qualities[0].Attempts);
            if(old is not null){Assert.Equal(1,old.Snapshot().FreshEncoded);Assert.Equal(1,old.Snapshot().Qualities[0].Attempts);}
        }
        finally { EfficiencyDiagnosticOptions.Apply(authority,4,null,0); }
    }
    private sealed class Encoder(Action transition) : ID215JpegEncoder
    {
        public ReadOnlySpan<byte> Encode(ReadOnlySpan<byte> frame){transition();return new byte[]{9};}
        public void Dispose() { }
    }
}
