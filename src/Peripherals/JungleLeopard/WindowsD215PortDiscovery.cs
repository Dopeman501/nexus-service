#if WINDOWS
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Nexus.Service.Devices.Detection.Native;

namespace Nexus.Service.Peripherals.JungleLeopard;

/// <summary>Reads current Plug and Play metadata without opening a serial device.</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsD215PortDiscovery : ID215PortDiscovery
{
    internal static readonly Guid ComPortInterface = new("86e0d1e0-8089-11d0-9ce4-08003e301f73");

    private const uint HardwareIdProperty = 0x02; // CM_DRP_HARDWAREID
    private const uint CapabilitiesProperty = 0x10; // CM_DRP_CAPABILITIES
    private const uint UniqueId = 0x10; // CM_DEVCAP_UNIQUEID
    private const uint RegistryDword = 4;
    private const uint RegistryMultiString = 7;
    private const uint KeyQueryValue = 0x01;
    private const uint OpenExisting = 0x01; // RegDisposition_OpenExisting
    private const uint HardwareKey = 0; // CM_REGISTRY_HARDWARE

    public WindowsD215PortDiscovery() { }

    public IReadOnlyList<D215Port> Discover()
    {
        var candidates = new List<D215PortProperties>();
        foreach (var instanceId in CfgMgr32.GetPresentDeviceIds("USB"))
        {
            if (!D215PortDiscovery.IsTargetInterfaceInstance(instanceId)) continue;
            if (CfgMgr32.LocateDevNode(instanceId, out var node) != CfgMgr32.CR_SUCCESS)
            {
                candidates.Add(new D215PortProperties(instanceId, "", "", "", "", false));
                continue;
            }

            var hardwareIds = ReadHardwareIds(node);
            var portName = ReadPortName(node);
            var path = CfgMgr32.GetInterfacePath(instanceId, ComPortInterface);
            if (!CfgMgr32.GetInterfaceInstanceId(path).Equals(instanceId, StringComparison.OrdinalIgnoreCase))
                path = "";

            var parentInstanceId = "";
            var parentUnique = false;
            if (CM_Get_Parent(out var parent, node, 0) == CfgMgr32.CR_SUCCESS)
            {
                parentInstanceId = CfgMgr32.GetStringProperty(parent, CfgMgr32.DEVPKEY_Device_InstanceId);
                parentUnique = HasUniqueId(parent);
            }
            candidates.Add(new D215PortProperties(instanceId, hardwareIds, portName, path, parentInstanceId, parentUnique));
        }
        return D215PortDiscovery.SelectPorts(candidates);
    }

    private static unsafe string ReadHardwareIds(uint node)
    {
        Span<byte> buffer = stackalloc byte[4096];
        var size = (uint)buffer.Length;
        fixed (byte* data = buffer)
        {
            if (CM_Get_DevNode_Registry_Property(node, HardwareIdProperty, out var type, data, ref size, 0) != CfgMgr32.CR_SUCCESS
                || type != RegistryMultiString || size > buffer.Length || (size & 1) != 0)
            {
                return "";
            }
        }
        return Encoding.Unicode.GetString(buffer[..(int)size]);
    }

    private static unsafe bool HasUniqueId(uint node)
    {
        uint capabilities = 0;
        uint size = sizeof(uint);
        return CM_Get_DevNode_Registry_Property(node, CapabilitiesProperty, out var type,
            (byte*)&capabilities, ref size, 0) == CfgMgr32.CR_SUCCESS
            && type == RegistryDword && size == sizeof(uint) && (capabilities & UniqueId) != 0;
    }

    private static string ReadPortName(uint node)
    {
        if (CM_Open_DevNode_Key(node, KeyQueryValue, 0, OpenExisting, out var rawKey, HardwareKey) != CfgMgr32.CR_SUCCESS)
            return "";

        using var handle = new SafeRegistryHandle(rawKey, ownsHandle: true);
        try
        {
            using var key = RegistryKey.FromHandle(handle);
            return key.GetValue("PortName", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // The device can disappear or lose its accessible metadata during a scan.
            return "";
        }
    }

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Parent(out uint parent, uint node, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Registry_PropertyW")]
    private static unsafe partial uint CM_Get_DevNode_Registry_Property(uint node, uint property,
        out uint type, byte* buffer, ref uint size, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Open_DevNode_Key(uint node, uint access, uint hardwareProfile,
        uint disposition, out IntPtr key, uint flags);
}
#endif
