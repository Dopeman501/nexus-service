using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Effects;
using Nexus.Service.Media;
using Nexus.Service.Serialization;

namespace Nexus.Service.Lighting;

/// <summary>
/// Two bounded RGB clips, shared by lightweight look wrappers. One worker loads
/// only the latest requested item; render callbacks never open or read a file.
/// </summary>
internal sealed class LightingAudioTriggerMediaResources : IDisposable
{
    private sealed record Entry(MediaFramesEffect Effect, long Used);
    internal const int MaximumBytes = MediaImporter.CanvasWidth * MediaImporter.CanvasHeight * 3 * MediaImporter.MaxFrames;
    private readonly object _gate = new();
    private readonly Func<string, CancellationToken, Task<MediaFramesEffect>> _load;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    private CancellationTokenSource? _loading;
    private string? _loadingId;
    private string? _desired;
    private long _generation;
    private long _used;
    private Task _worker = Task.CompletedTask;
    private bool _working;
    private bool _disposed;

    internal LightingAudioTriggerMediaResources(MediaLibrary media) : this((id, token) => LoadAsync(media, id, token)) { }
    internal LightingAudioTriggerMediaResources(Func<string, CancellationToken, Task<MediaFramesEffect>> load) => _load = load;

    internal Task PendingLoad { get { lock (_gate) return _worker; } }

    internal bool TryRender(string id, CanvasBuffer canvas, double tick)
    {
        lock (_gate)
        {
            if (_disposed || !MediaLibrary.IsValidId(id)) return false;
            _desired = id;
            if (_loadingId != id) _loading?.Cancel();
            if (_entries.TryGetValue(id, out var entry))
            {
                _entries[id] = entry with { Used = ++_used };
                entry.Effect.RenderFrame(canvas, tick);
                return true;
            }
            if (!_failed.Contains(id) && !_working)
            {
                _working = true;
                _worker = Task.Run(LoadLatestAsync);
            }
            return false;
        }
    }

    private async Task LoadLatestAsync()
    {
        while (true)
        {
            string id;
            long generation;
            CancellationTokenSource cancellation;
            lock (_gate)
            {
                if (_disposed || _desired is null || _entries.ContainsKey(_desired) || _failed.Contains(_desired))
                {
                    _working = false;
                    return;
                }
                id = _desired;
                generation = _generation;
                // Leave room before allocating the next clip: one retained plus
                // one incoming buffer, each capped at MaximumBytes.
                while (_entries.Count > 1)
                {
                    var oldest = _entries.MinBy(pair => pair.Value.Used);
                    oldest.Value.Effect.Dispose();
                    _entries.Remove(oldest.Key);
                }
                cancellation = new CancellationTokenSource();
                _loading = cancellation;
                _loadingId = id;
            }
            MediaFramesEffect? loaded = null;
            Exception? failure = null;
            try { loaded = await _load(id, cancellation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex) { failure = ex; }
            lock (_gate)
            {
                _loading = null;
                _loadingId = null;
                if (!_disposed && generation == _generation && _desired == id && !cancellation.IsCancellationRequested)
                {
                    if (loaded is not null)
                    {
                        _entries[id] = new(loaded, ++_used);
                        loaded = null;
                    }
                    else if (failure is not null)
                    {
                        _failed.Add(id);
                        Console.Error.WriteLine($"[lighting-audio-trigger] media '{id}' failed: {failure.Message}");
                    }
                }
            }
            loaded?.Dispose();
            loaded = null;
            cancellation.Dispose();
        }
    }

    internal void Reset()
    {
        lock (_gate)
        {
            _generation++;
            _desired = null;
            _loading?.Cancel();
            foreach (var entry in _entries.Values) entry.Effect.Dispose();
            _entries.Clear();
            _failed.Clear();
        }
    }

    private static async Task<MediaFramesEffect> LoadAsync(MediaLibrary media, string id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Files can be edited through the library folder. Bound metadata as well
        // as frame allocations, including a file growing while it is read.
        var metadata = await ReadBoundedAsync(Path.Combine(media.GetItemDir(id), "meta.json"), 16 * 1024, null, token).ConfigureAwait(false);
        var item = JsonSerializer.Deserialize(metadata, AppJsonContext.Default.MediaItem)
            ?? throw new InvalidDataException("Media is unavailable.");
        if (item.Id != id || item.Width != MediaImporter.CanvasWidth || item.Height != MediaImporter.CanvasHeight
            || item.Frames is < 1 or > MediaImporter.MaxFrames || item.Fps is < 0 or > MediaImporter.MaxFps
            || (item.Frames > 1 && item.Fps == 0))
        {
            throw new InvalidDataException("Media frame metadata is invalid.");
        }
        var expected = checked(item.Width * item.Height * 3 * item.Frames);
        var frames = await ReadBoundedAsync(media.GetFramesBinPath(id), MaximumBytes, expected, token).ConfigureAwait(false);
        return new MediaFramesEffect(frames, item.Width, item.Height, item.Fps);
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int maximumBytes, int? expected, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        if (length < 1 || length > maximumBytes || (expected is { } size && length != size))
        {
            throw new InvalidDataException("Media file size is invalid.");
        }
        var bytes = new byte[(int)length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        // Reject an append during loading without growing the bounded buffer.
        if (await stream.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0)
            throw new InvalidDataException("Media changed during loading.");
        return bytes;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Reset();
        }
    }
}

/// <summary>The cached look owns no frame bytes; resources bound all media look memory together.</summary>
internal sealed class LightingAudioTriggerMediaEffect(LightingAudioTriggerMediaResources resources, string id) : IEffect
{
    public string Name => "media";
    internal bool DrewFrame { get; private set; }
    public void RenderFrame(CanvasBuffer canvas, double tickMs) => DrewFrame = resources.TryRender(id, canvas, tickMs);
    public void Dispose() { }
}
