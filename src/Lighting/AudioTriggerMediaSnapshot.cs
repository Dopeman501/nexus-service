using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Nexus.Service.Lighting;

internal sealed class AudioTriggerMediaSnapshot(string path, int frames, int fps)
{
    internal const int FrameBytes = 160 * 90 * 3;
    internal string Path { get; } = path;
    internal int Frames { get; } = frames;
    internal int Fps { get; } = fps;
    internal int Length => checked(Frames * FrameBytes);
    private SafeFileHandle? _handle;

    // Called on the snapshot worker after the copy is closed. Only an owned
    // immutable cache file stays open; no library-original handle survives it.
    internal void Bind()
    {
        AudioTriggerMediaPathGuard.Check(Path);
        _handle = File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
    }

    internal async Task ReadFrameAsync(int index, Memory<byte> destination, CancellationToken token)
    {
        if ((uint)index >= (uint)Frames || destination.Length != FrameBytes)
            throw new ArgumentOutOfRangeException(nameof(index));
        await ReadExactlyAsync(destination, (long)index * FrameBytes, token).ConfigureAwait(false);
    }

    internal async Task ReadAllAsync(Memory<byte> destination, CancellationToken token)
    {
        if (destination.Length != Length) throw new ArgumentException("Invalid media buffer size.", nameof(destination));
        await ReadExactlyAsync(destination, 0, token).ConfigureAwait(false);
    }

    private async Task ReadExactlyAsync(Memory<byte> destination, long offset, CancellationToken token)
    {
        var handle = _handle ?? throw new ObjectDisposedException(nameof(AudioTriggerMediaSnapshot));
        while (!destination.IsEmpty)
        {
            var count = await RandomAccess.ReadAsync(handle, destination, offset, token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("The owned media snapshot is incomplete.");
            destination = destination[count..];
            offset += count;
        }
    }

    // The session waits for the actual read continuation before deleting or
    // disposing its store, so this never closes beneath a native read.
    internal void Close() => Interlocked.Exchange(ref _handle, null)?.Dispose();
}
