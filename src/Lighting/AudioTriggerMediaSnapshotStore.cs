using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Media;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

namespace Nexus.Service.Lighting;

/// <summary>Worker-only disk snapshots. An exclusive owner and durable inventory bound live and orphan bytes together.</summary>
internal sealed class AudioTriggerMediaSnapshotStore : IDisposable
{
    internal const long MaximumDiskBytes = 512L * 1024 * 1024;
    private const int MaximumInventoryBytes = 1_048_576;
    private readonly object _gate = new();
    private readonly string _root;
    private readonly long _maximum;
    private readonly FileStream _owner;
    private readonly Dictionary<string, long> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AudioTriggerMediaSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cleanupWarnings = new(StringComparer.Ordinal);
    private long _reserved;

    internal AudioTriggerMediaSnapshotStore(string root, long maximumBytes = MaximumDiskBytes)
    {
        _root = System.IO.Path.GetFullPath(root);
        _maximum = maximumBytes;
        AudioTriggerMediaPathGuard.Check(_root);
        if (OperatingSystem.IsWindows() && WindowsDirectorySecurity.IsLocalSystem()
            && Directory.Exists(_root) && !WindowsDirectorySecurity.IsOwnedByAdmins(_root))
        {
            throw new IOException("The existing media cache is not owned by the service.");
        }
        Directory.CreateDirectory(_root);
        if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The media cache must be an owned directory.");
        if (OperatingSystem.IsWindows() && WindowsDirectorySecurity.IsLocalSystem())
        {
            WindowsDirectorySecurity.Protect(_root, resetOwner: true);
            if (!WindowsDirectorySecurity.IsOwnedByAdmins(_root)) throw new IOException("The media cache owner is invalid.");
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var ownerPath = OwnedPath(".owner");
        _owner = new FileStream(ownerPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try { Recover(); }
        catch { _owner.Dispose(); throw; }
    }

    internal long ReservedBytes { get { lock (_gate) return _reserved; } }

    internal async Task<AudioTriggerMediaSnapshot> CreateAsync(MediaLibrary media, string id, byte[] scratch, CancellationToken token)
    {
        if (!MediaLibrary.IsValidId(id) || scratch.Length != 65536) throw new ArgumentException("Invalid media snapshot request.");
        var metadataPath = System.IO.Path.Combine(media.GetItemDir(id), "meta.json");
        AudioTriggerMediaPathGuard.Check(metadataPath);
        await using var metadataStream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous);
        if (metadataStream.Length is < 1 or > 16384) throw new InvalidDataException("Media metadata size is invalid.");
        var metadata = new byte[(int)metadataStream.Length];
        await metadataStream.ReadExactlyAsync(metadata, token).ConfigureAwait(false);
        if (await metadataStream.ReadAsync(scratch.AsMemory(0, 1), token).ConfigureAwait(false) != 0)
            throw new InvalidDataException("Media metadata changed during loading.");
        var item = JsonSerializer.Deserialize(metadata, AppJsonContext.Default.MediaItem)
            ?? throw new InvalidDataException("Media is unavailable.");
        if (item.Id != id || item.Width != MediaImporter.CanvasWidth || item.Height != MediaImporter.CanvasHeight
            || item.Frames is < 1 or > MediaImporter.MaxFrames || item.Fps is < 0 or > MediaImporter.MaxFps
            || (item.Frames > 1 && item.Fps == 0)) throw new InvalidDataException("Media frame metadata is invalid.");
        var length = checked(item.Frames * AudioTriggerMediaSnapshot.FrameBytes);
        var name = Guid.NewGuid().ToString("N") + ".rgb";
        Reserve(name, length);
        var snapshot = new AudioTriggerMediaSnapshot(OwnedPath(name), item.Frames, item.Fps);
        try
        {
            await CopyAsync(media.GetFramesBinPath(id), snapshot.Path, length, scratch, token).ConfigureAwait(false);
            snapshot.Bind();
            lock (_gate) _snapshots.Add(name, snapshot);
            return snapshot;
        }
        catch
        {
            Delete(snapshot);
            throw;
        }
    }

    private static async Task CopyAsync(string source, string target, int length, byte[] scratch, CancellationToken token)
    {
        AudioTriggerMediaPathGuard.Check(source);
        AudioTriggerMediaPathGuard.Check(target);
        // Originals are locked only for the bounded copy. Later rendering opens only the owned file.
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length != length) throw new InvalidDataException("Media file size is invalid.");
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var remaining = length;
        while (remaining > 0)
        {
            var count = Math.Min(remaining, scratch.Length);
            await input.ReadExactlyAsync(scratch.AsMemory(0, count), token).ConfigureAwait(false);
            await output.WriteAsync(scratch.AsMemory(0, count), token).ConfigureAwait(false);
            remaining -= count;
        }
        if (await input.ReadAsync(scratch.AsMemory(0, 1), token).ConfigureAwait(false) != 0)
            throw new InvalidDataException("Media changed during loading.");
        await output.FlushAsync(token).ConfigureAwait(false);
    }

    private void Reserve(string name, int length)
    {
        lock (_gate)
        {
            if (length > _maximum - _reserved) throw new IOException("The Audio Trigger media snapshot budget is exhausted.");
            _files.Add(name, length);
            _reserved += length;
            try { SaveInventory(); }
            catch
            {
                _files.Remove(name);
                _cleanupWarnings.Remove(name);
                _reserved -= length;
                throw;
            }
        }
    }

    internal void Delete(AudioTriggerMediaSnapshot snapshot)
    {
        lock (_gate)
        {
            var name = System.IO.Path.GetFileName(snapshot.Path);
            if (!_files.TryGetValue(name, out var length) || snapshot.Path != System.IO.Path.Combine(_root, name))
                throw new InvalidOperationException("The snapshot is not owned by this cache.");
            try
            {
                if (_snapshots.Remove(name, out var retained)) retained.Close();
                snapshot.Close();
                File.Delete(OwnedPath(name));
                _files.Remove(name);
                _cleanupWarnings.Remove(name);
                _reserved -= length;
                SaveInventory();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Only a file still owned by this cache needs a retained warning key.
                // Inventory failure after successful deletion must not accumulate old GUIDs.
                var stillOwned = _files.ContainsKey(name);
                if (stillOwned && !_cleanupWarnings.Add(name)) return;
                var operation = stillOwned ? "owned snapshot cleanup" : "snapshot inventory refresh";
                Console.Error.WriteLine($"[lighting-audio-trigger] {operation} deferred ({error.GetType().Name}).");
            }
        }
    }

    private void Recover()
    {
        var inventory = OwnedPath(".inventory");
        if (File.Exists(inventory))
        {
            if (new FileInfo(inventory).Length > MaximumInventoryBytes) throw new InvalidDataException("Media cache inventory is oversized.");
            foreach (var line in File.ReadAllLines(inventory))
            {
                var parts = line.Split('|');
                if (parts.Length != 2 || parts[0].Length != 36 || !parts[0].EndsWith(".rgb", StringComparison.Ordinal)
                    || !Guid.TryParseExact(parts[0][..32], "N", out _) || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                    || size is < AudioTriggerMediaSnapshot.FrameBytes or > LightingAudioTriggerMediaResources.MaximumBytes)
                    throw new InvalidDataException("Media cache inventory is invalid.");
                _files.Add(parts[0], size);
                _reserved = checked(_reserved + size);
            }
        }
        foreach (var pair in new List<KeyValuePair<string, long>>(_files))
            Delete(new AudioTriggerMediaSnapshot(System.IO.Path.Combine(_root, pair.Key), (int)(pair.Value / AudioTriggerMediaSnapshot.FrameBytes), 0));
        if (_reserved > _maximum) throw new IOException("The media cache cannot recover within its disk budget.");
    }

    private void SaveInventory()
    {
        var content = new StringBuilder();
        foreach (var pair in _files) content.Append(pair.Key).Append('|').Append(pair.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        var pending = OwnedPath(".inventory.pending");
        // Unlink stale entries first. CreateNew must never truncate a planted
        // hard link to a file outside this cache, even though it is not a reparse point.
        File.Delete(pending);
        using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(content);
        File.Move(pending, OwnedPath(".inventory"), overwrite: true);
    }

    private string OwnedPath(string name)
    {
        if (System.IO.Path.GetFileName(name) != name) throw new InvalidDataException("Invalid owned cache name.");
        var path = System.IO.Path.Combine(_root, name);
        AudioTriggerMediaPathGuard.Check(path);
        return path;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var snapshot in _snapshots.Values) snapshot.Close();
            _snapshots.Clear();
            _owner.Dispose();
        }
    }
}
