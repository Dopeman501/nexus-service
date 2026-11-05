using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nexus.Service.Peripherals.JungleLeopard;

internal sealed record D215Port(string PortName, string Identity, bool StableIdentity, string? DevicePath = null);

internal interface ID215PortDiscovery
{
    IReadOnlyList<D215Port> Discover();
}

internal readonly record struct D215PortProperties(string InstanceId, string HardwareIds, string PortName,
    string DevicePath, string ParentInstanceId, bool ParentHasUniqueId);

internal static class D215PortDiscovery
{
    internal const int VendorId = 0x33C3;
    internal const int ProductId = 0x7792;

    private const string ParentHardwareId = @"USB\VID_33C3&PID_7792";
    private const string InterfaceSuffix = "&MI_00";
    private const string RevisionPrefix = ParentHardwareId + "&REV_";

    internal static IReadOnlyList<D215Port> SelectPorts(IEnumerable<D215PortProperties> candidates)
    {
        var ports = new List<D215Port>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var presentInterfaces = 0;
        foreach (var candidate in candidates)
        {
            if (!IsTargetInterfaceInstance(candidate.InstanceId)) continue;
            presentInterfaces++;
            if (CreatePort(candidate) is not { } port) continue;

            if (!identities.Add(port.Identity) || !aliases.Add(port.PortName) || !paths.Add(candidate.DevicePath))
                return [];
            ports.Add(port);
        }

        // Without verified IDs for every present interface, multiple devices
        // cannot be distinguished safely. A single fallback is COM-bound.
        if (presentInterfaces > 1 && (ports.Count != presentInterfaces || ports.Exists(static port => !port.StableIdentity)))
            return [];
        return ports.ToArray();
    }

    internal static bool IsTargetInterfaceInstance(string instanceId)
        => TrySplitInstanceId(instanceId, out var hardwareId) && IsTargetHardwareId(hardwareId);

    private static D215Port? CreatePort(D215PortProperties candidate)
    {
        if (!HasTargetHardwareId(candidate.HardwareIds) || !IsPortAlias(candidate.PortName)
            || !candidate.DevicePath.StartsWith(@"\\?\", StringComparison.Ordinal)
            || candidate.DevicePath.Length <= 4 || candidate.DevicePath.Contains('\0'))
        {
            return null;
        }

        var portName = candidate.PortName.ToUpperInvariant();
        var stable = candidate.ParentHasUniqueId
            && TrySplitInstanceId(candidate.ParentInstanceId, out var parentHardwareId)
            && parentHardwareId.Equals(ParentHardwareId, StringComparison.OrdinalIgnoreCase);
        var identitySource = stable ? candidate.ParentInstanceId : $"{candidate.InstanceId}|{portName}";
        var identityHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identitySource.ToUpperInvariant())));
        var identity = stable ? $"usb-{identityHash}" : $"local-{identityHash}";
        return new D215Port(portName, identity, stable, candidate.DevicePath);
    }

    private static bool IsPortAlias(string alias)
        => alias.Length >= 4 && alias.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            && alias[3] is >= '1' and <= '9' && !alias.Contains('\0')
            && int.TryParse(alias.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out _);

    private static bool HasTargetHardwareId(string ids)
    {
        var list = ids.AsSpan();
        foreach (var range in list.Split('\0'))
        {
            if (IsTargetHardwareId(list[range])) return true;
        }
        return false;
    }

    private static bool IsTargetHardwareId(ReadOnlySpan<char> id)
    {
        if (id.Equals(ParentHardwareId + InterfaceSuffix, StringComparison.OrdinalIgnoreCase)) return true;
        if (id.Length != RevisionPrefix.Length + 4 + InterfaceSuffix.Length
            || !id.StartsWith(RevisionPrefix, StringComparison.OrdinalIgnoreCase)
            || !id.EndsWith(InterfaceSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        foreach (var digit in id.Slice(RevisionPrefix.Length, 4))
        {
            if (!char.IsAsciiHexDigit(digit)) return false;
        }
        return true;
    }

    private static bool TrySplitInstanceId(string instanceId, out ReadOnlySpan<char> hardwareId)
    {
        hardwareId = default;
        if (!instanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) return false;
        var separator = instanceId.IndexOf('\\', startIndex: 4);
        if (separator < 0 || separator == instanceId.Length - 1
            || instanceId.AsSpan(separator + 1).IndexOfAny('\\', '\0') >= 0)
        {
            return false;
        }
        hardwareId = instanceId.AsSpan(0, separator);
        return true;
    }
}
