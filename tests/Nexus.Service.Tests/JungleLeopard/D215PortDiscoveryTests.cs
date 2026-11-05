using System.Collections.Generic;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Peripherals.JungleLeopard;

namespace Nexus.Service.Tests.JungleLeopard;

public class D215PortDiscoveryTests
{
    private const string HardwareId = @"USB\VID_33C3&PID_7792&MI_00";
    private const string ParentId = @"USB\VID_33C3&PID_7792\PANEL-SERIAL";

    [Fact]
    public void Match_uses_every_hardware_id_in_the_Windows_list()
    {
        var row = Candidate(hardwareIds: "USB\\UNKNOWN\0usb\\vid_33c3&pid_7792&rev_0100&mi_00\0\0");

        var port = Assert.Single(D215PortDiscovery.SelectPorts([row]));

        Assert.Equal("COM3", port.PortName);
        Assert.Equal(row.DevicePath, port.DevicePath);
    }

    [Theory]
    [InlineData(@"USB\VID_33C3&PID_7792")]
    [InlineData(@"USB\VID_33C3&PID_7792&MI_01")]
    [InlineData(@"USB\VID_33C3&PID_7793&MI_00")]
    [InlineData(@"USB\VID_43C3&PID_7792&MI_00")]
    [InlineData(@"USB\VID_33C3&PID_7792&MI_000")]
    [InlineData(@"USB\VID_33C3&PID_7792&REV_ZZZZ&MI_00")]
    [InlineData(@"HID\VID_33C3&PID_7792&MI_00")]
    public void Match_rejects_other_hardware_ids(string hardwareId)
    {
        Assert.Empty(D215PortDiscovery.SelectPorts([Candidate(hardwareIds: hardwareId)]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("COM0")]
    [InlineData("COM01")]
    [InlineData("COM-1")]
    [InlineData("COM3x")]
    [InlineData("COM2147483648")]
    [InlineData(@"\\.\COM3")]
    public void Match_requires_a_canonical_port_alias(string portName)
    {
        Assert.Empty(D215PortDiscovery.SelectPorts([Candidate(portName: portName)]));
    }

    [Fact]
    public void Match_rejects_an_unverified_device_path_or_mismatched_instance()
    {
        var row = Candidate();
        Assert.Empty(D215PortDiscovery.SelectPorts([row with { DevicePath = "" }]));
        Assert.Empty(D215PortDiscovery.SelectPorts([row with { DevicePath = @"\\.\COM3" }]));
        Assert.Empty(D215PortDiscovery.SelectPorts([row with { InstanceId = @"USB\VID_33C3&PID_7792&MI_01\OTHER" }]));
    }

    [Fact]
    public void Verified_parent_identity_survives_port_reassignment()
    {
        var first = Assert.Single(D215PortDiscovery.SelectPorts([Candidate()]));
        var second = Assert.Single(D215PortDiscovery.SelectPorts([Candidate(portName: "com41", instanceSuffix: "7&PORT&0001", parentId: ParentId.ToLowerInvariant())]));

        Assert.True(first.StableIdentity);
        Assert.True(second.StableIdentity);
        Assert.Equal(first.Identity, second.Identity);
        Assert.Equal("COM41", second.PortName);
        Assert.DoesNotContain("PANEL-SERIAL", first.Identity);
    }

    [Theory]
    [InlineData(ParentId, false)]
    [InlineData(@"USB\VID_33C3&PID_7793\OTHER", true)]
    [InlineData(@"USB\VID_33C3&PID_7792\", true)]
    [InlineData(@"USB\VID_33C3&PID_7792&MI_01\OTHER", true)]
    public void Missing_parent_proof_yields_a_single_port_bound_candidate(string parentId, bool uniqueId)
    {
        var row = Candidate(parentId: parentId, parentHasUniqueId: uniqueId);
        var first = Assert.Single(D215PortDiscovery.SelectPorts([row]));
        var second = Assert.Single(D215PortDiscovery.SelectPorts([row with { PortName = "COM4" }]));

        Assert.False(first.StableIdentity);
        Assert.NotEqual(first.Identity, second.Identity);
    }

    [Fact]
    public void Discovery_declines_multiple_port_bound_candidates()
    {
        Assert.Empty(D215PortDiscovery.SelectPorts([
            Candidate(parentHasUniqueId: false),
            Candidate(portName: "COM4", instanceSuffix: "7&OTHER&0000", parentHasUniqueId: false),
        ]));
    }

    [Fact]
    public void Discovery_rejects_duplicate_identities_and_aliases()
    {
        Assert.Empty(D215PortDiscovery.SelectPorts([Candidate(), Candidate(portName: "COM4", instanceSuffix: "7&DUPLICATE&0000")]));
        Assert.Empty(D215PortDiscovery.SelectPorts([Candidate(), Candidate(portName: "com3", instanceSuffix: "7&DUPLICATE&0000", parentId: ParentId + "2")]));
        var first = Candidate();
        var second = Candidate(portName: "COM4", instanceSuffix: "7&OTHER&0000", parentId: ParentId + "2");
        Assert.Empty(D215PortDiscovery.SelectPorts([first, second with { DevicePath = first.DevicePath }]));
    }

    [Fact]
    public void Discovery_returns_distinct_verified_devices_for_ambiguity_detection()
    {
        var ports = D215PortDiscovery.SelectPorts([
            Candidate(),
            Candidate(portName: "COM4", instanceSuffix: "7&OTHER&0000", parentId: ParentId + "2"),
        ]);

        Assert.Collection(ports, first => Assert.Equal("COM3", first.PortName), second => Assert.Equal("COM4", second.PortName));
    }

    [Theory]
    [InlineData("COM4")]
    [InlineData("")]
    public void Discovery_declines_ownership_if_another_matching_interface_is_unverified(string secondAlias)
    {
        var second = Candidate(portName: secondAlias, instanceSuffix: "7&OTHER&0000", parentHasUniqueId: false);

        Assert.Empty(D215PortDiscovery.SelectPorts([Candidate(), second]));
    }

    [Fact]
    public void Handler_reports_presence_through_read_only_discovery()
    {
        var absent = new JungleLeopardD215Handler(new FakeDiscovery([]));
        var present = new JungleLeopardD215Handler(new FakeDiscovery([new D215Port("COM3", "test-identity", false)]));

        Assert.False(absent.IsConnected([]));
        Assert.True(present.IsConnected([]));
        Assert.Equal("jungle-leopard-d215", present.Id);
        Assert.Equal("", present.GetFirmwareVersion());
    }

    private static D215PortProperties Candidate(string hardwareIds = HardwareId, string portName = "COM3",
        string instanceSuffix = "7&PORT&0000", string parentId = ParentId, bool parentHasUniqueId = true) => new(
            $@"{HardwareId}\{instanceSuffix}", hardwareIds, portName,
            $@"\\?\USB#VID_33C3&PID_7792&MI_00#{instanceSuffix}#{{86e0d1e0-8089-11d0-9ce4-08003e301f73}}",
            parentId, parentHasUniqueId);

    private sealed class FakeDiscovery(IReadOnlyList<D215Port> ports) : ID215PortDiscovery
    {
        public IReadOnlyList<D215Port> Discover() => ports;
    }
}
