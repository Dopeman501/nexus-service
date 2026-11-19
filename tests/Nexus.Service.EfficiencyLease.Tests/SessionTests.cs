using Nexus.Service.Diagnostics;
namespace Nexus.Service.Tests.EfficiencyLease;

public sealed class SessionTests
{
    private const string Run = "22222222222222222222222222222222";
    [Fact] public void Valid_start_duplicate_and_expiry_keep_original_deadline()
    {
        long now = 0; var calls = new List<EfficiencyLeaseProjection>();
        using var owner = new EfficiencyDiagnosticSession(() => now, () => DateTimeOffset.UnixEpoch, calls.Add, false);
        var request = new EfficiencyLeaseRequest { ExpectedServiceGeneration=owner.Generation, ExpectedRevision=0, RunId=Run, DurationSeconds=30 };
        Assert.Equal(200, owner.Start(request, out var first)); Assert.True(first.NativeActive);
        now = 10_000; Assert.Equal(200, owner.Start(request, out var same)); Assert.Equal(first.ExpiresUtc, same.ExpiresUtc); Assert.Equal(20_000, same.RemainingMs);
        now = 30_000; Assert.False(owner.Snapshot().NativeActive); Assert.Equal(2, calls.Count); Assert.Equal(409, owner.Start(request,out _));
    }
    [Theory] [InlineData(0)] [InlineData(29)] [InlineData(2101)]
    public void Invalid_duration_does_not_activate(int seconds)
    {
        using var owner = new EfficiencyDiagnosticSession(schedule:false);
        Assert.Equal(400,owner.Start(new(){ExpectedServiceGeneration=owner.Generation,RunId=Run,DurationSeconds=seconds},out _));
        Assert.False(owner.Snapshot().NativeActive);
    }
    [Fact] public void Authority_revision_conflict_cancel_and_ack_are_scoped()
    {
        using var owner = new EfficiencyDiagnosticSession(schedule:false);
        var body=new EfficiencyLeaseRequest {ExpectedServiceGeneration=owner.Generation,RunId=Run,DurationSeconds=2100};
        Assert.Equal(200, owner.Start(body,out var active));
        Assert.Equal(409,owner.Start(new(){ExpectedServiceGeneration=owner.Generation,ExpectedRevision=active.Revision,RunId="33333333333333333333333333333333",DurationSeconds=30},out _));
        Assert.Equal(409,owner.Acknowledge(new(){ServiceGeneration=owner.Generation,Revision=active.Revision,RunId=Run,OverlayInstance="bad"}));
        Assert.Equal(200,owner.Acknowledge(new(){ServiceGeneration=owner.Generation,Revision=active.Revision,RunId=Run,OverlayInstance="44444444444444444444444444444444"}));
        body.ExpectedRevision=active.Revision;Assert.Equal(200,owner.Cancel(body,out var closed));Assert.False(closed.NativeActive);
        Assert.Equal(409,owner.Acknowledge(new(){ServiceGeneration=owner.Generation,Revision=active.Revision,RunId=Run,OverlayInstance="44444444444444444444444444444444"}));
    }
    [Fact] public void Rate_limit_uses_injected_monotonic_clock()
    {
        long now=0;using var owner=new EfficiencyDiagnosticSession(()=>now,schedule:false);
        Assert.True(owner.AcceptRequest());Assert.False(owner.AcceptRequest());now=250;Assert.True(owner.AcceptRequest());
    }
    [Fact] public void Broken_activation_or_retirement_callback_cannot_escape_or_leave_a_live_lease()
    {
        using var broken = new EfficiencyDiagnosticSession(apply:_=>throw new IOException("private-log-failure"),schedule:false);
        Assert.Equal(503,broken.Start(new(){ExpectedServiceGeneration=broken.Generation,RunId=Run,DurationSeconds=30},out var state));
        Assert.False(state.NativeActive);
        long now=0;var fail=false;
        using var expiry=new EfficiencyDiagnosticSession(()=>now,apply:_=>{if(fail)throw new IOException();},schedule:false);
        Assert.Equal(200,expiry.Start(new(){ExpectedServiceGeneration=expiry.Generation,RunId=Run,DurationSeconds=30},out _));
        fail=true;now=30000;Assert.False(expiry.Snapshot().NativeActive);
    }
    [Fact] public async Task Active_expiry_watchdog_retires_without_any_http_poll()
    {
        long now=0;var retired=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var owner=new EfficiencyDiagnosticSession(()=>Interlocked.Read(ref now),apply:s=>{if(!s.NativeActive)retired.TrySetResult();});
        Assert.Equal(200,owner.Start(new(){ExpectedServiceGeneration=owner.Generation,RunId=Run,DurationSeconds=30},out _));
        Interlocked.Exchange(ref now,30000);
        await retired.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(owner.Snapshot().NativeActive);
    }
}
