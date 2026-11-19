using System;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Structural completeness only: the protocol has no transaction token proving
/// freshness. Valid prefixes still supply positive observations and page learning.
/// </summary>
internal readonly record struct Slv3DeviceListObservation(
    bool HeaderAccepted, int DeclaredRecords, int AvailableRecords, int ValidRecords, bool Complete)
{
    internal const int MaximumPages = 3;

    internal static Slv3DeviceListObservation Read(ReadOnlySpan<byte> reply, int requestedPages)
    {
        if (requestedPages is < 1 or > MaximumPages
            || reply.Length < Slv3Protocol.RecordHeaderLength || reply[0] != Slv3Protocol.UsbSendRf)
            return default;

        var declared = Slv3Protocol.RecordCount(reply);
        var boundedBytes = Math.Min(reply.Length, Slv3Protocol.PageLength * requestedPages);
        var available = Math.Min(declared,
            (boundedBytes - Slv3Protocol.RecordHeaderLength) / Slv3Protocol.RecordLength);
        var valid = 0;
        for (var i = 0; i < available; i++)
        {
            var validator = Slv3Protocol.RecordHeaderLength + (i + 1) * Slv3Protocol.RecordLength - 1;
            if (reply[validator] == Slv3Protocol.RecordValidator) valid++;
        }
        return new(true, declared, available, valid,
            reply.Length <= Slv3Protocol.PageLength * requestedPages && available == declared && valid == declared);
    }
}
