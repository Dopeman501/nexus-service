using System;
using System.IO;

namespace Nexus.Service.Peripherals.JungleLeopard;

internal enum D215ConnectionStatus
{
    Absent,
    Available,
    Connecting,
    Identified,
    Ambiguous,
    Busy,
    NoResponse,
    Unsupported,
    InvalidReply,
    PortChanged,
    OpenTimedOut,
    WriteTimedOut,
    IoFailure,
    CleanupFailed,
    Releasing,
    ReleaseTimedOut,
}

internal sealed record D215ConnectionSnapshot(D215Port? Port, D215DeviceInfo? Info, D215ConnectionStatus Status)
{
    internal string? Warning => Status switch
    {
        D215ConnectionStatus.Ambiguous => "More than one Jungle Leopard D215 is present. Connect one display to continue.",
        D215ConnectionStatus.Busy => "Port busy or access denied. Exit Jungle Leopard Display from its system tray, then retry Nexus Control.",
        D215ConnectionStatus.NoResponse => "Display detected, no response. Reconnect it or retry Nexus Control.",
        D215ConnectionStatus.Unsupported => "This Jungle Leopard model or firmware is unsupported.",
        D215ConnectionStatus.InvalidReply => "The display returned invalid device information.",
        D215ConnectionStatus.PortChanged => "The display port changed during identification. Reconnect the display.",
        D215ConnectionStatus.OpenTimedOut or D215ConnectionStatus.WriteTimedOut or D215ConnectionStatus.IoFailure
            => "The display connection failed. Reconnect it or retry Nexus Control.",
        D215ConnectionStatus.CleanupFailed => "The display port could not be released safely. Restart Nexus before retrying.",
        D215ConnectionStatus.Releasing => "Nexus is waiting for Windows to release the D215 port.",
        D215ConnectionStatus.ReleaseTimedOut => "Windows has not released the D215 port. Nexus will wait for cleanup before reconnecting.",
        _ => null,
    };
}

internal sealed class D215ConnectionException(D215ConnectionStatus status, string message, Exception? innerException = null)
    : IOException(message, innerException)
{
    internal D215ConnectionStatus Status { get; } = status;
}
