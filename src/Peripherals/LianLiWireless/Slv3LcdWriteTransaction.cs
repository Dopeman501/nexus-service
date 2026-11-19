#if WINDOWS
using System;
using System.Runtime.InteropServices;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>Native write boundary; it never opens or discovers a device.</summary>
internal interface ISlv3LcdWriteIo
{
    GCHandle Pin(byte[] buffer);
    IntPtr AllocateOverlapped();
    void InitializeOverlapped(IntPtr storage);
    bool Write(GCHandle pin, int length, IntPtr storage, out uint transferred, out int error);
    uint Wait(uint milliseconds);
    void Abort();
    /// <summary>Waits for terminal native completion, including an unsuccessful operation.</summary>
    bool Reap(IntPtr storage, out uint transferred);
    void FreeOverlapped(IntPtr storage);
    void Unpin(GCHandle pin);
}

internal static class Slv3LcdWriteTransaction
{
    internal const uint WriteTimeoutMs = 3000;
    internal static bool Write(ISlv3LcdWriteIo io, byte[] buffer)
    {
        GCHandle pin = default;
        var storage = IntPtr.Zero;
        try
        {
            pin = io.Pin(buffer);
            storage = io.AllocateOverlapped();
            if (storage == IntPtr.Zero) throw new OutOfMemoryException("Could not allocate LCD OVERLAPPED storage.");
            io.InitializeOverlapped(storage);
            var ok = io.Write(pin, buffer.Length, storage, out var transferred, out var error);
            if (!ok && error == (int)Slv3WinUsbInterop.ERROR_IO_PENDING)
            {
                // A wait/abort outcome is not completion. The blocking reap remains
                // authoritative before either pin/storage or packet can be reused.
                try
                {
                    if (io.Wait(WriteTimeoutMs) != 0) io.Abort();
                }
                finally
                {
                    // Managed wait/abort boundary failures also have to reach
                    // terminal native completion before releasing resources.
                    ok = io.Reap(storage, out transferred);
                }
            }
            return ok && transferred == buffer.Length;
        }
        finally
        {
            try
            {
                if (storage != IntPtr.Zero) io.FreeOverlapped(storage);
            }
            finally
            {
                if (pin.IsAllocated) io.Unpin(pin);
            }
        }
    }
}

internal sealed class Slv3LcdNativeWriteIo(IntPtr interfaceHandle, IntPtr writeEvent) : ISlv3LcdWriteIo
{
    public GCHandle Pin(byte[] buffer) => GCHandle.Alloc(buffer, GCHandleType.Pinned);
    public IntPtr AllocateOverlapped() => Marshal.AllocHGlobal(Marshal.SizeOf<System.Threading.NativeOverlapped>());
    public void InitializeOverlapped(IntPtr storage)
    {
        Marshal.StructureToPtr(new System.Threading.NativeOverlapped { EventHandle = writeEvent }, storage, false);
        Slv3WinUsbInterop.ResetEvent(writeEvent);
    }
    public bool Write(GCHandle pin, int length, IntPtr storage, out uint transferred, out int error)
    {
        var ok = Slv3WinUsbInterop.WinUsb_WritePipe(interfaceHandle, Slv3LcdProtocol.WritePipeId,
            pin.AddrOfPinnedObject(), (uint)length, out transferred, storage);
        error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }
    public uint Wait(uint milliseconds) => Slv3WinUsbInterop.WaitForSingleObject(writeEvent, milliseconds);
    public void Abort() => Slv3WinUsbInterop.WinUsb_AbortPipe(interfaceHandle, Slv3LcdProtocol.WritePipeId);
    public bool Reap(IntPtr storage, out uint transferred) =>
        Slv3WinUsbInterop.WinUsb_GetOverlappedResult(interfaceHandle, storage, out transferred, wait: true);
    public void FreeOverlapped(IntPtr storage) => Marshal.FreeHGlobal(storage);
    public void Unpin(GCHandle pin) => pin.Free();
}
#endif
