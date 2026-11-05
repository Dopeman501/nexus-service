#if WINDOWS
using System;
using System.Buffers;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Nexus.Service.Devices.Detection.Native;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.JungleLeopard;

/// <summary>One exclusive Windows handle for the discovered D215 serial interface.</summary>
internal sealed class WindowsD215SerialTransport : ID215SerialTransport
{
    private const uint BaudRate = 2_000_000;
    private const uint ReadIdleTimeoutMs = 120;
    private const uint WriteTimeoutMs = 2_000;

    private readonly SafeFileHandle _handle;
    private readonly ThreadPoolBoundHandle _binding;
    private readonly SemaphoreSlim _ioGate = new(1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationToken _stoppingToken;
    private readonly object _disposeLock = new();
    private Task? _disposeTask;

    private WindowsD215SerialTransport(SafeFileHandle handle, ThreadPoolBoundHandle binding)
    {
        _handle = handle;
        _binding = binding;
        _stoppingToken = _stopping.Token;
    }

    internal static ValueTask<ID215SerialTransport> OpenAsync(D215Port port, CancellationToken cancellationToken)
    {
        var path = port.DevicePath;
        if (path is null || path.Length <= 4 || !path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.Contains('\0') || path.Contains(':'))
        {
            throw new ArgumentException("A discovered D215 serial device interface is required.", nameof(port));
        }

        // FILE_FLAG_OVERLAPPED applies to I/O after opening. CreateFile and serial setup may
        // block in a driver. Keep the open task alive until it returns and can release its handle.
        return new ValueTask<ID215SerialTransport>(Task.Run(() => Open(path, cancellationToken), cancellationToken));
    }

    private static ID215SerialTransport Open(string path, CancellationToken cancellationToken)
    {
        var instanceId = CfgMgr32.GetInterfaceInstanceId(path);
        if (!D215PortDiscovery.IsTargetInterfaceInstance(instanceId)
            || !string.Equals(CfgMgr32.GetInterfacePath(instanceId, WindowsD215PortDiscovery.ComPortInterface),
                path, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The discovered D215 serial interface is no longer present.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var handle = Native.CreateFileW(path, Native.GenericRead | Native.GenericWrite, shareMode: 0,
            IntPtr.Zero, Native.OpenExisting, Native.FileFlagOverlapped, IntPtr.Zero);
        ThreadPoolBoundHandle? binding = null;
        try
        {
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error is Native.ErrorAccessDenied or Native.ErrorSharingViolation or Native.ErrorLockViolation)
                {
                    throw new UnauthorizedAccessException(
                        "The D215 serial port is busy or access was denied. If the vendor app is running, use its tray Exit before retrying.",
                        new Win32Exception(error));
                }
                throw NativeError("open", error);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Configure(handle);
            cancellationToken.ThrowIfCancellationRequested();
            binding = ThreadPoolBoundHandle.BindHandle(handle);
            cancellationToken.ThrowIfCancellationRequested();
            return new WindowsD215SerialTransport(handle, binding);
        }
        catch
        {
            try { binding?.Dispose(); }
            finally { handle.Dispose(); }
            throw;
        }
    }

    private static void Configure(SafeFileHandle handle)
    {
        var dcb = new Native.Dcb { Length = (uint)Marshal.SizeOf<Native.Dcb>() };
        if (!Native.GetCommState(handle, ref dcb))
            throw NativeError("read serial settings", Marshal.GetLastPInvokeError());

        // Keep driver-provided limits and characters. Set all control bits explicitly so a
        // previous owner cannot leave hardware/software handshaking enabled.
        dcb.BaudRate = BaudRate;
        dcb.Flags = Native.Binary | Native.DtrEnable | Native.RtsEnable;
        dcb.ByteSize = 8;
        dcb.Parity = Native.NoParity;
        dcb.StopBits = Native.OneStopBit;
        if (!Native.SetCommState(handle, in dcb))
            throw NativeError("configure serial settings", Marshal.GetLastPInvokeError());

        // Return buffered data immediately, or wait briefly for the first byte.
        var timeouts = new Native.CommTimeouts
        {
            ReadInterval = uint.MaxValue,
            ReadMultiplier = uint.MaxValue,
            ReadConstant = ReadIdleTimeoutMs,
            WriteConstant = WriteTimeoutMs,
        };
        if (!Native.SetCommTimeouts(handle, in timeouts))
            throw NativeError("configure serial timeouts", Marshal.GetLastPInvokeError());
        if (!Native.PurgeComm(handle, Native.PurgeReadBuffer | Native.PurgeWriteBuffer))
            throw NativeError("clear stale serial data", Marshal.GetLastPInvokeError());
    }

    public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
        => TransferAsync(destination, write: false, cancellationToken);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        var count = await TransferAsync(source, write: true, cancellationToken).ConfigureAwait(false);
        if (count != source.Length)
            throw new IOException("The D215 serial write ended before the complete request was sent.");
    }

    private async ValueTask<int> TransferAsync(ReadOnlyMemory<byte> buffer, bool write, CancellationToken cancellationToken)
    {
        _stoppingToken.ThrowIfCancellationRequested();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stoppingToken);
        // A driver may block while issuing even an overlapped request.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        await _ioGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            if (buffer.IsEmpty) return 0;

            using var transfer = new PendingTransfer(_handle, _binding, buffer, linked.Token);
            transfer.Submit(write);
            var count = await transfer.Completion.ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            return count;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            if (_disposeTask is null)
            {
                var canceled = _stopping.CancelAsync();
                // CloseHandle is synchronous. Leave the task pending until Windows releases
                // the handle, so the owner can keep the port quarantined if close stalls.
                _disposeTask = Task.Run(() => DisposeCoreAsync(canceled));
            }
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task canceled)
    {
        try
        {
            await canceled.ConfigureAwait(false);
        }
        finally
        {
            await _ioGate.WaitAsync().ConfigureAwait(false);
            try { _binding.Dispose(); }
            finally
            {
                _handle.Dispose();
                _stopping.Dispose();
                _ioGate.Release();
            }
        }
    }

    private static IOException NativeError(string operation, int error)
        => new($"D215 serial {operation} failed with Windows error {error}.", new Win32Exception(error));

    private sealed unsafe class PendingTransfer : IDisposable
    {
        private static readonly IOCompletionCallback OnCompletion = Complete;

        private readonly SafeFileHandle _handle;
        private readonly ThreadPoolBoundHandle _binding;
        private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationToken _cancellationToken;
        private readonly MemoryHandle _pin;
        private readonly NativeOverlapped* _overlapped;
        private readonly int _length;
        private CancellationTokenRegistration _registration;

        internal PendingTransfer(SafeFileHandle handle, ThreadPoolBoundHandle binding, ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken)
        {
            _handle = handle;
            _binding = binding;
            _cancellationToken = cancellationToken;
            _length = buffer.Length;
            _pin = buffer.Pin();
            try
            {
                _overlapped = binding.UnsafeAllocateNativeOverlapped(OnCompletion, this, null);
            }
            catch
            {
                _pin.Dispose();
                throw;
            }
        }

        internal Task<int> Completion => _completion.Task;

        internal void Submit(bool write)
        {
            _registration = _cancellationToken.UnsafeRegister(static state => ((PendingTransfer)state!).Cancel(), this);
            _cancellationToken.ThrowIfCancellationRequested();
            var success = write
                ? Native.WriteFile(_handle, _pin.Pointer, (uint)_length, IntPtr.Zero, _overlapped)
                : Native.ReadFile(_handle, _pin.Pointer, (uint)_length, IntPtr.Zero, _overlapped);
            if (!success)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != Native.ErrorIoPending)
                    throw NativeError(write ? "write" : "read", error);
            }

            // Cancellation may have reached the registration before the OS queued the I/O.
            if (_cancellationToken.IsCancellationRequested) Cancel();
        }

        private void Cancel()
        {
            if (Native.CancelIoEx(_handle, _overlapped)) return;
            var error = Marshal.GetLastPInvokeError();
            if (error != Native.ErrorNotFound)
                ServiceLog.Warn($"[d215] serial cancellation request failed with Windows error {error}");
        }

        private static void Complete(uint error, uint count, NativeOverlapped* overlapped)
        {
            var transfer = (PendingTransfer)ThreadPoolBoundHandle.GetNativeOverlappedState(overlapped)!;
            if (error == Native.ErrorOperationAborted && transfer._cancellationToken.IsCancellationRequested)
                transfer._completion.TrySetCanceled(transfer._cancellationToken);
            else if (error != 0)
                transfer._completion.TrySetException(NativeError("transfer", (int)error));
            else if (count > (uint)transfer._length)
                transfer._completion.TrySetException(new IOException("The D215 driver reported an invalid serial transfer length."));
            else
                transfer._completion.TrySetResult((int)count);
        }

        public void Dispose()
        {
            // Cancellation requests do not complete native I/O. The await above keeps the
            // pin and OVERLAPPED alive until completion, and disposal joins a racing callback.
            _registration.Dispose();
            _binding.FreeNativeOverlapped(_overlapped);
            _pin.Dispose();
        }
    }

    private static class Native
    {
        internal const uint GenericRead = 0x80000000;
        internal const uint GenericWrite = 0x40000000;
        internal const uint OpenExisting = 3;
        internal const uint FileFlagOverlapped = 0x40000000;
        internal const uint Binary = 1;
        internal const uint DtrEnable = 1 << 4;
        internal const uint RtsEnable = 1 << 12;
        internal const byte NoParity = 0;
        internal const byte OneStopBit = 0;
        internal const uint PurgeWriteBuffer = 0x0004;
        internal const uint PurgeReadBuffer = 0x0008;
        internal const int ErrorAccessDenied = 5;
        internal const int ErrorSharingViolation = 32;
        internal const int ErrorLockViolation = 33;
        internal const int ErrorOperationAborted = 995;
        internal const int ErrorIoPending = 997;
        internal const int ErrorNotFound = 1168;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Dcb
        {
            internal uint Length;
            internal uint BaudRate;
            internal uint Flags;
            internal ushort Reserved;
            internal ushort XonLimit;
            internal ushort XoffLimit;
            internal byte ByteSize;
            internal byte Parity;
            internal byte StopBits;
            internal byte XonChar;
            internal byte XoffChar;
            internal byte ErrorChar;
            internal byte EofChar;
            internal byte EventChar;
            internal ushort Reserved1;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct CommTimeouts
        {
            internal uint ReadInterval;
            internal uint ReadMultiplier;
            internal uint ReadConstant;
            internal uint WriteMultiplier;
            internal uint WriteConstant;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string path, uint access, uint shareMode,
            IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCommState(SafeFileHandle handle, ref Dcb dcb);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetCommState(SafeFileHandle handle, in Dcb dcb);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetCommTimeouts(SafeFileHandle handle, in CommTimeouts timeouts);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PurgeComm(SafeFileHandle handle, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern unsafe bool ReadFile(SafeFileHandle handle, void* buffer, uint count,
            IntPtr transferred, NativeOverlapped* overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern unsafe bool WriteFile(SafeFileHandle handle, void* buffer, uint count,
            IntPtr transferred, NativeOverlapped* overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern unsafe bool CancelIoEx(SafeFileHandle handle, NativeOverlapped* overlapped);
    }
}
#endif
