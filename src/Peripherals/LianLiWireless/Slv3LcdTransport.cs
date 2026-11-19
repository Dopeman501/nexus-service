using System;
using System.Collections.Generic;
#if WINDOWS
using System.IO;
using System.Runtime.InteropServices;
#endif

namespace Nexus.Service.Peripherals.LianLiWireless;

public interface ISlv3LcdDiscovery
{
    IReadOnlyList<Slv3LcdPortInfo> Discover();
}

public sealed class Slv3LcdPortInfo
{
    public required string PortName { get; init; }
    public string Serial { get; init; } = "";
    public int ProductId { get; init; }
}

/// <summary>One open SL-LCD Wireless screen: a WinUSB bulk pipe pair, EP 0x01 OUT / EP 0x81 IN.</summary>
public interface ISlv3LcdTransport : IDisposable
{
    bool IsOpen { get; }
    string PortName { get; }

    /// <summary>Pushes a 400x400 JPEG as one fixed-size bulk write. False on a transport failure.</summary>
    bool PushImage(byte[] jpeg);

    /// <summary>Header-only write, no JPEG payload.</summary>
    bool SetBrightness(byte value);

    /// <summary>Header-only write; value is 0..3 for the four panel rotations.</summary>
    bool SetRotation(byte value);

    /// <summary>Queries GetPosIndex(201); on success, <paramref name="groupIndex"/> is the fan-position group from ack byte 8.</summary>
    bool TryGetPosition(out byte groupIndex);
}

#if WINDOWS
/// <summary>
/// WinUSB-backed transport for one SL-LCD Wireless screen. Reuses
/// <see cref="Slv3WinUsbInterop"/>, the same WinUSB P/Invoke surface as the
/// RF dongles, but the bulk OUT pipe here rejects a NULL-overlapped write with
/// ERROR_BAD_COMMAND, so writes go through a real OVERLAPPED + GetOverlappedResult
/// (the RF interrupt pipes tolerate NULL). A background thread drains the IN
/// endpoint continuously, matching L-Connect's DataReceived reader.
/// </summary>
public sealed class Slv3LcdTransport : ISlv3LcdTransport
{
    // A 102400-byte bulk write is a single WinUsb_WritePipe call; this bounds
    // a stalled write instead of blocking indefinitely.
    private const uint WriteTimeoutMs = Slv3LcdWriteTransaction.WriteTimeoutMs;
    // The ack read is optional (plan section 4.1); a short timeout reads a
    // non-acking screen as "no ack" rather than blocking the caller.
    private const int AckReadTimeoutMs = 500;

    private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _fileHandle;
    private readonly IntPtr _winUsbHandle;
    private readonly IntPtr _writeEvent;
    private readonly ISlv3LcdWriteIo _writeIo;
    private readonly bool _ownsDeviceHandles = true;
    private readonly object _ioLock = new();
    private byte[]? _transferPacket;
    private bool _disposed;

    // One OUT write at a time (all writes hold _ioLock), so a single reusable
    // event + overlapped buffer suffices.
    private bool WriteOverlappedLocked(byte[] buffer) => Slv3LcdWriteTransaction.Write(_writeIo, buffer);

    // Inert test seam: no device open/discovery, IN drain, native handles or logging.
    internal Slv3LcdTransport(ISlv3LcdWriteIo writeIo)
    {
        _writeIo = writeIo ?? throw new ArgumentNullException(nameof(writeIo));
        _fileHandle = new Microsoft.Win32.SafeHandles.SafeFileHandle(new IntPtr(1), ownsHandle: false);
        _ownsDeviceHandles = false;
        _loggedFirstPush = true;
        PortName = "fake-lcd";
    }

    public Slv3LcdTransport(string devicePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        PortName = devicePath;

        _fileHandle = Slv3WinUsbInterop.CreateFileW(
            devicePath,
            Slv3WinUsbInterop.GENERIC_READ | Slv3WinUsbInterop.GENERIC_WRITE,
            Slv3WinUsbInterop.FILE_SHARE_READ | Slv3WinUsbInterop.FILE_SHARE_WRITE,
            IntPtr.Zero,
            Slv3WinUsbInterop.OPEN_EXISTING,
            Slv3WinUsbInterop.FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);
        if (_fileHandle.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            _fileHandle.Dispose();
            throw new IOException($"CreateFileW failed for {devicePath}: {err}");
        }

        if (!Slv3WinUsbInterop.WinUsb_Initialize(_fileHandle, out _winUsbHandle))
        {
            var err = Marshal.GetLastWin32Error();
            _fileHandle.Dispose();
            throw new IOException($"WinUsb_Initialize failed for {devicePath}: {err}");
        }

        _writeEvent = Slv3WinUsbInterop.CreateEventW(IntPtr.Zero, manualReset: true, initialState: false, IntPtr.Zero);
        if (_writeEvent == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            Slv3WinUsbInterop.WinUsb_Free(_winUsbHandle);
            _fileHandle.Dispose();
            throw new IOException($"CreateEventW failed for {devicePath}: {err}");
        }

        _writeIo = new Slv3LcdNativeWriteIo(_winUsbHandle, _writeEvent);
        var writeTimeout = WriteTimeoutMs;
        var readTimeout = (uint)AckReadTimeoutMs;
        if (!Slv3WinUsbInterop.WinUsb_SetPipePolicy(_winUsbHandle, Slv3LcdProtocol.WritePipeId,
            Slv3WinUsbInterop.PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref writeTimeout))
        {
            Nexus.Service.Platform.ServiceLog.Warn(
                $"[lianli-wireless-lcd] SetPipePolicy (write timeout) failed for {devicePath}: {Marshal.GetLastWin32Error()}");
        }
        if (!Slv3WinUsbInterop.WinUsb_SetPipePolicy(_winUsbHandle, Slv3LcdProtocol.ReadPipeId,
            Slv3WinUsbInterop.PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref readTimeout))
        {
            Nexus.Service.Platform.ServiceLog.Warn(
                $"[lianli-wireless-lcd] SetPipePolicy (read timeout) failed for {devicePath}: {Marshal.GetLastWin32Error()}");
        }

        // Clear any halt left on the pipes from L-Connect's prior session; a
        // stalled bulk OUT pipe rejects WinUsb_WritePipe with ERROR_BAD_COMMAND.
        Slv3WinUsbInterop.WinUsb_ResetPipe(_winUsbHandle, Slv3LcdProtocol.WritePipeId);
        Slv3WinUsbInterop.WinUsb_ResetPipe(_winUsbHandle, Slv3LcdProtocol.ReadPipeId);

        // The panel pushes back-pressure/acks on the IN endpoint; if the host does
        // not continuously drain it the pipe backs up and the OUT writes stall
        // (ERROR_BAD_COMMAND). L-Connect's InitDev runs a continuous DataReceived
        // reader for exactly this - mirror it with a background drain thread.
        _draining = true;
        _drainThread = new System.Threading.Thread(DrainLoop) { IsBackground = true, Name = "slv3-lcd-drain" };
        _drainThread.Start();

        // L-Connect's InitDev issues SetFrameRate(120) on open; the panel stays on
        // its boot logo and ignores pushed JPEGs until it receives this.
        WriteArgCommand(Slv3LcdProtocol.CmdType.SetFrameRate, 120);
    }

    private volatile bool _draining;
    private readonly System.Threading.Thread? _drainThread;
    // Sole reader of the IN pipe: TryGetPosition consumes acks from here rather
    // than issuing its own read, which would race the drain for the same frame.
    private readonly System.Collections.Concurrent.BlockingCollection<byte[]> _rxQueue = new(boundedCapacity: 32);

    private void DrainLoop()
    {
        var buf = new byte[10240];
        while (_draining && !_disposed)
        {
            // Different pipe (0x81) than writes (0x01) - WinUSB serializes per
            // pipe, so this reads concurrently with a push without _ioLock.
            if (Slv3WinUsbInterop.WinUsb_ReadPipe(
                    _winUsbHandle, Slv3LcdProtocol.ReadPipeId, buf, (uint)buf.Length, out var n, IntPtr.Zero)
                && n > 0)
            {
                var frame = buf[..(int)n];
                while (!_rxQueue.TryAdd(frame) && _rxQueue.TryTake(out _))
                {
                    // Drop the oldest ack to make room for the newest.
                }
            }
        }
    }

    public bool IsOpen => !_disposed && !_fileHandle.IsInvalid && !_fileHandle.IsClosed;
    public string PortName { get; }

    public bool PushImage(byte[] jpeg)
    {
        if (_disposed)
        {
            return false;
        }
        lock (_ioLock)
        {
            // The hub can drop this transport from another thread; Dispose frees the handles under this lock.
            if (_disposed)
            {
                return false;
            }
            // One packet per transport, held unchanged until the native write is reaped.
            // Formatting belongs inside the same lock as image/control writes and disposal.
            var buffer = _transferPacket ??= new byte[Slv3LcdProtocol.TransferBufferSize];
            var timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Slv3LcdProtocol.WritePushJpgBuffer(timestamp, jpeg, buffer);
            var ok = WriteOverlappedLocked(buffer);
            if (ok && !_loggedFirstPush)
            {
                _loggedFirstPush = true;
                Nexus.Service.Platform.ServiceLog.Info(
                    $"[lianli-wireless-lcd] PushImage OK ({buffer.Length} bytes)");
            }
            return ok;
        }
    }

    private bool _loggedFirstPush;

    public bool SetBrightness(byte value) => WriteArgCommand(Slv3LcdProtocol.CmdType.BrigthSet, value);

    public bool SetRotation(byte value) => WriteArgCommand(Slv3LcdProtocol.CmdType.Rotate, value);

    public bool TryGetPosition(out byte groupIndex)
    {
        groupIndex = 0;
        if (_disposed)
        {
            return false;
        }
        var timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var header = Slv3LcdProtocol.BuildArgCommandHeader(Slv3LcdProtocol.CmdType.GetPosIndex, timestamp, 0);
        lock (_ioLock)
        {
            if (_disposed)
            {
                return false;
            }
            // Discard acks queued before this query so the next frame is ours.
            while (_rxQueue.TryTake(out _))
            {
            }
            if (!WriteOverlappedLocked(header))
            {
                return false;
            }
            if (!_rxQueue.TryTake(out var ack, AckReadTimeoutMs) || ack.Length < 9)
            {
                return false;
            }
            groupIndex = ack[8];
            return true;
        }
    }

    private bool WriteArgCommand(Slv3LcdProtocol.CmdType cmd, byte value)
    {
        if (_disposed)
        {
            return false;
        }
        var timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var header = Slv3LcdProtocol.BuildArgCommandHeader(cmd, timestamp, value);
        lock (_ioLock)
        {
            return !_disposed && WriteOverlappedLocked(header);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        // Stop the drain thread before freeing the handle it reads from. Abort
        // the IN pipe so a blocked ReadPipe returns even if its timeout policy
        // never took, then join before WinUsb_Free (no read in flight = no UAF).
        _draining = false;
        if (_ownsDeviceHandles) Slv3WinUsbInterop.WinUsb_AbortPipe(_winUsbHandle, Slv3LcdProtocol.ReadPipeId);
        _drainThread?.Join();
        lock (_ioLock)
        {
            _rxQueue.Dispose();
            _transferPacket = null;
            if (_winUsbHandle != IntPtr.Zero)
            {
                Slv3WinUsbInterop.WinUsb_Free(_winUsbHandle);
            }
            _fileHandle.Dispose();
            if (_writeEvent != IntPtr.Zero)
            {
                Slv3WinUsbInterop.CloseHandle(_writeEvent);
            }
        }
    }
}
#else
/// <summary>Inert: the LCD screens are Windows-only for v1.</summary>
public sealed class Slv3LcdTransport : ISlv3LcdTransport
{
    public Slv3LcdTransport(string devicePath)
    {
        PortName = devicePath;
    }

    public bool IsOpen => false;
    public string PortName { get; }
    public bool PushImage(byte[] jpeg) => false;
    public bool SetBrightness(byte value) => false;
    public bool SetRotation(byte value) => false;

    public bool TryGetPosition(out byte groupIndex)
    {
        groupIndex = 0;
        return false;
    }

    public void Dispose()
    {
    }
}
#endif
