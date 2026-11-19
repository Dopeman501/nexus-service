using System.Runtime.CompilerServices;

namespace Nexus.Service.Tests.EfficiencyLease;

internal static class LeaseBootstrapSnapshot
{
    internal static bool Expected, ServiceEnabled, OverlayEnabled;
    internal static long ServiceEpoch, OverlayEpoch;
    [ModuleInitializer]
    internal static void CaptureBeforeAnyRuntimeApply()
    {
        Expected=Environment.GetEnvironmentVariable("NEXUS_EFFICIENCY_DIAGNOSTICS")=="1";
        ServiceEnabled=Diagnostics.EfficiencyDiagnosticOptions.Enabled;
        ServiceEpoch=Diagnostics.EfficiencyDiagnosticOptions.SessionRevision;
        OverlayEnabled=Nexus.Overlay.EfficiencyDiagnosticOptions.Enabled;
        OverlayEpoch=Nexus.Overlay.EfficiencyDiagnosticOptions.SessionRevision;
    }
}

public sealed class BootstrapTests
{
    [Fact]
    public void Exact_child_process_environment_controls_only_initial_bounded_bootstrap()
    {
        Assert.Equal(LeaseBootstrapSnapshot.Expected,LeaseBootstrapSnapshot.ServiceEnabled);
        Assert.Equal(LeaseBootstrapSnapshot.Expected,LeaseBootstrapSnapshot.OverlayEnabled);
        var expected=LeaseBootstrapSnapshot.Expected?long.MaxValue:0;
        Assert.Equal(expected,LeaseBootstrapSnapshot.ServiceEpoch);Assert.Equal(expected,LeaseBootstrapSnapshot.OverlayEpoch);
    }
}
