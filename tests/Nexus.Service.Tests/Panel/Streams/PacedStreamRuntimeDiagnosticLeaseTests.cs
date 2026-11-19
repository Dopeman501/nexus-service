using System.Reflection;
using System.Runtime.CompilerServices;
using Nexus.Service.Diagnostics;
using Nexus.Service.Panel.Streams;

namespace Nexus.Service.Tests.Panel.Streams;

[Collection("Runtime diagnostic lease")]
public sealed class PacedStreamRuntimeDiagnosticLeaseTests
{
    private delegate bool Write(PacedStreamWriter writer,IStreamedPanelTransport transport,ReadOnlySpan<byte> bytes,bool idr);
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task In_flight_transport_write_keeps_its_original_diagnostic_owner(bool initiallyActive)
    {
        var authority=Guid.NewGuid().ToString("N");EfficiencyDiagnosticOptions.Apply(authority,0,null,0);
        if(initiallyActive)EfficiencyDiagnosticOptions.Apply(authority,1,"11111111111111111111111111111111",30000);
        var session=new StreamSession("unit-session",new StreamedPanelDeviceInfo{Serial="unit",Profile=new(){Kind="unit",DisplayName="unit",Surface="monitor",CssWidth=2,CssHeight=2,Fps=30,Codec=StreamCodec.RawBgra}},"unit-panel");
        var old=session.Diagnostics;
        // Build only the actual Write operation; never start the writer thread,
        // timer-resolution P/Invoke, transport discovery or a physical transport.
        var writer=(PacedStreamWriter)RuntimeHelpers.GetUninitializedObject(typeof(PacedStreamWriter));
        typeof(PacedStreamWriter).GetField("_session",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(writer,session);
        var write=typeof(PacedStreamWriter).GetMethod("TryWrite",BindingFlags.Instance|BindingFlags.NonPublic)!.CreateDelegate<Write>();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release=new ManualResetEventSlim();
        var transport=new Transport(()=>{entered.TrySetResult();if(!release.Wait(TimeSpan.FromSeconds(5)))throw new TimeoutException();});
        Task<bool>? operation=null;
        try
        {
            operation=Task.Run(()=>write(writer,transport,new byte[]{1,2},true));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            EfficiencyDiagnosticOptions.Apply(authority,2,null,0);
            EfficiencyDiagnosticOptions.Apply(authority,3,"33333333333333333333333333333333",30000);
            release.Set();Assert.True(await operation.WaitAsync(TimeSpan.FromSeconds(5)));
            var current=Assert.IsType<StreamStageDiagnostics>(session.Diagnostics);Assert.NotSame(old,current);
            Assert.Equal(0,current.Snapshot().TransportCalls);Assert.Equal(0,current.Snapshot().TransportCall.Count);
            if(old is not null)Assert.Equal(1,old.Snapshot().TransportCalls);
        }
        finally{release.Set();if(operation is not null)await operation.WaitAsync(TimeSpan.FromSeconds(5));EfficiencyDiagnosticOptions.Apply(authority,4,null,0);}
    }
    private sealed class Transport(Action duringWrite) : IStreamedPanelTransport
    {
        public bool IsOpen=>true;public string Serial=>"unit";
        public void Open()=>throw new InvalidOperationException();public void StartPlayer()=>throw new InvalidOperationException();
        public void Write(ReadOnlySpan<byte> bytes)=>duringWrite();public void Dispose() { }
    }
}
