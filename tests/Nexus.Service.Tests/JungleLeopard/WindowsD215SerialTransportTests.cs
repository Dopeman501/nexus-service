#if WINDOWS
using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Peripherals.JungleLeopard;

namespace Nexus.Service.Tests.JungleLeopard;

public sealed class WindowsD215SerialTransportTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("COM3")]
    [InlineData(@"\\.\COM3")]
    [InlineData(@"C:\temp\file")]
    [InlineData(@"\\?\C:\temp\file")]
    [InlineData("\\\\?\\USB#example\0suffix")]
    public async Task Open_rejects_a_port_without_a_device_interface(string? path)
    {
        var port = new D215Port("COM3", "fixture", false, path);

        await Assert.ThrowsAsync<ArgumentException>(
            () => WindowsD215SerialTransport.OpenAsync(port, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task A_cancelled_open_does_not_attempt_to_resolve_the_interface()
    {
        var port = new D215Port("COM3", "fixture", false,
            @"\\?\USB#VID_33C3&PID_7792&MI_00#fixture#{86E0D1E0-8089-11D0-9CE4-08003E301F73}");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => WindowsD215SerialTransport.OpenAsync(port, new CancellationToken(canceled: true)).AsTask());
    }
}
#endif
