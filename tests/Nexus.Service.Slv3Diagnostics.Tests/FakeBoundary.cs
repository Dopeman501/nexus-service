using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Nexus.Service.Peripherals.LianLiWireless
{
    // Source-linked tests deliberately replace the ENTIRE native boundary.
    // No P/Invoke, device discovery or connection worker is present.
    internal static class Slv3WinUsbInterop
    {
        public const uint GENERIC_READ = 1, GENERIC_WRITE = 2, FILE_SHARE_READ = 1,
            FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000,
            PIPE_TRANSFER_TIMEOUT = 3;
        public static readonly Queue<(bool Ok, byte[] Bytes, int Error)> Reads = new();
        public static int ReadCalls, FlushCalls, WriteCalls, Frees;
        public static bool FlushSucceeds = true, WriteSucceeds = true;
        public static int FlushError, WriteError;
        public static uint? WriteLength;
        public static readonly List<byte[]> Sent = new();
        public static void Reset()
        {
            Reads.Clear(); Sent.Clear(); ReadCalls = FlushCalls = WriteCalls = Frees = 0;
            FlushSucceeds = WriteSucceeds = true; FlushError = WriteError = 0; WriteLength = null;
            Nexus.Service.Platform.ServiceLog.Messages.Clear();
        }
        public static SafeFileHandle CreateFileW(string path, uint a, uint b, IntPtr c,
            uint d, uint e, IntPtr f) => new(new IntPtr(123), ownsHandle: false);
        public static bool WinUsb_Initialize(SafeFileHandle file, out IntPtr handle)
        { handle = new IntPtr(456); return true; }
        public static bool WinUsb_SetPipePolicy(IntPtr h, byte p, uint t, uint l, ref uint value) => true;
        public static bool WinUsb_FlushPipe(IntPtr h, byte p)
        { FlushCalls++; Marshal.SetLastPInvokeError(FlushError); return FlushSucceeds; }
        public static bool WinUsb_WritePipe(IntPtr h, byte p, byte[] b, uint l,
            out uint written, IntPtr o)
        { WriteCalls++; Sent.Add(b.ToArray()); written = WriteLength ?? l; Marshal.SetLastPInvokeError(WriteError); return WriteSucceeds; }
        public static bool WinUsb_ReadPipe(IntPtr h, byte p, byte[] b, uint l,
            out uint read, IntPtr o)
        {
            ReadCalls++;
            if (!Reads.TryDequeue(out var item)) { read = 0; Marshal.SetLastPInvokeError(121); return false; }
            read = (uint)item.Bytes.Length; item.Bytes.CopyTo(b, 0);
            Marshal.SetLastPInvokeError(item.Error); return item.Ok;
        }
        public static bool WinUsb_Free(IntPtr h) { Frees++; return true; }
    }
}
namespace Nexus.Service.Platform
{
    // The unrelated lock-state hosted bridge is compiled but never started.
    public sealed class SessionLockListener
    {
        public event Action<bool>? LockChanged { add { } remove { } }
    }
    internal static class ServiceLog
    {
        public static readonly ConcurrentQueue<string> Messages = new();
        public static void Info(string message) => Messages.Enqueue(message);
        public static void Warn(string message) => Messages.Enqueue(message);
        public static void Error(string message) => Messages.Enqueue(message);
    }
}
namespace Nexus.Service.Peripherals.Hyte.Np50
{
    // Unrelated RGB value dependency, used by the real RGB formatter source.
    public readonly record struct RgbColor(byte R, byte G, byte B);
}
namespace Nexus.Service.Lighting
{
    // Only immutable timing constants are needed; no blackout worker is linked.
    internal static class SleepBlackoutCoordinator
    {
        internal static readonly TimeSpan LockFadeDuration = TimeSpan.FromMilliseconds(1500);
        internal static readonly TimeSpan UnlockFadeDuration = TimeSpan.FromMilliseconds(900);
    }
}
