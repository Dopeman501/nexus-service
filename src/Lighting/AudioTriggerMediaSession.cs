using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Media;

namespace Nexus.Service.Lighting;

/// <summary>One configured-content generation; two persistent readers, bounded decoded storage and fair ID demand.</summary>
internal sealed class AudioTriggerMediaSession : IDisposable
{
    internal const int MaximumWindowBuffers = 96;
    internal const int MaximumDecodedBytes = 2 * LightingAudioTriggerMediaResources.MaximumBytes;
    private sealed class Entry(string id)
    {
        internal readonly string Id = id;
        internal AudioTriggerMediaSnapshot? Snapshot;
        internal AudioTriggerMediaClock Clock;
        internal byte[]? Hot, Front, Back, Ahead;
        internal int FrontIndex = -1, BackIndex = -1, AheadIndex = -1, DesiredFrame;
        internal bool Active, Retired, Failed, Queued, Loading;
        internal long Used;
        internal CancellationTokenSource? Reading;
        internal byte[]? InflightBuffer;
    }
    private enum Operation { Snapshot, Hot, Frame }
    private readonly record struct Job(Entry Entry, Operation Operation, byte[]? Buffer, int Index);
    private readonly object _gate = new();
    private readonly MediaLibrary _media;
    private readonly Func<string, CancellationToken, Task>? _beforeLoad;
    private readonly Func<string, int, CancellationToken, Task>? _beforeRead;
    private readonly Lazy<AudioTriggerMediaSnapshotStore> _store;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _demand = new(StringComparer.Ordinal);
    private readonly Queue<Entry> _queue = new();
    private readonly Queue<AudioTriggerMediaSnapshot> _deletes = new();
    private readonly Stack<byte[]> _spares = new();
    private readonly SemaphoreSlim _wake = new(0, 2);
    private readonly CancellationTokenSource _stop = new();
    private Task[] _workers = [];
    private TaskCompletionSource _idle = Completed();
    private Task? _completion;
    private bool _explicitDemand, _disposed;
    private int _decodedBytes, _windowBuffers, _inflight;
    private long _used;

    internal AudioTriggerMediaSession(MediaLibrary media, Func<string, CancellationToken, Task>? beforeLoad = null,
        Func<string, int, CancellationToken, Task>? beforeRead = null)
    {
        _media = media;
        _beforeLoad = beforeLoad;
        _beforeRead = beforeRead;
        _store = new(() => new AudioTriggerMediaSnapshotStore(media.RootDir + "-audio-trigger-cache"));
    }

    internal Task PendingLoad { get { lock (_gate) return _completion ?? _idle.Task; } }
    internal (int DecodedBytes, int WindowBuffers, int ActiveIds, int Pending, int Readers) Usage
    {
        get { lock (_gate) return (_decodedBytes, _windowBuffers, _demand.Count, _queue.Count, _inflight); }
    }

    internal void ReconcileDemand(IReadOnlyCollection<string> ids)
    {
        lock (_gate)
        {
            _explicitDemand = true;
            SetDemand(ids);
        }
    }

    private void SetDemand(IReadOnlyCollection<string> ids)
    {
        if (_disposed) return;
        if (ids.Count > LightingAudioTriggerPolicy.MaximumLooks || ids.Any(id => !MediaLibrary.IsValidId(id)))
            throw new ArgumentException("Invalid Audio Trigger media demand.", nameof(ids));
        if (_demand.SetEquals(ids)) return;
        _demand.Clear();
        foreach (var id in ids) _demand.Add(id);
        foreach (var entry in _entries.Values)
        {
            entry.Active = _demand.Contains(entry.Id);
            if (!entry.Active)
            {
                entry.Reading?.Cancel();
                ReleaseWindows(entry);
            }
        }
        foreach (var id in _demand)
        {
            if (!_entries.TryGetValue(id, out var entry))
            {
                if (_entries.Count >= LightingAudioTriggerPolicy.MaximumLooks)
                    throw new InvalidOperationException("Media content requires a configuration reset.");
                _entries.Add(id, entry = new(id));
            }
            entry.Active = true;
            Enqueue(entry);
        }
    }

    internal bool TryRender(string id, CanvasBuffer canvas, double tick)
    {
        lock (_gate)
        {
            if (_disposed || !MediaLibrary.IsValidId(id)) return false;
            if (!_explicitDemand) SetDemand(new[] { id });
            if (!_entries.TryGetValue(id, out var entry) || !entry.Active || entry.Snapshot is not { } snapshot) return false;
            var clock = entry.Clock.At(tick, snapshot.Frames, snapshot.Fps);
            var offset = clock.Frame * AudioTriggerMediaSnapshot.FrameBytes;
            if (entry.Hot is { } hot) canvas.WriteFromRgb(hot.AsSpan(offset, AudioTriggerMediaSnapshot.FrameBytes));
            else
            {
                var frame = FrameBuffer(entry, clock.Frame);
                if (frame is null)
                {
                    entry.DesiredFrame = clock.Frame;
                    Enqueue(entry);
                    return false;
                }
                canvas.WriteFromRgb(frame);
            }
            entry.Clock = clock;
            entry.Used = ++_used;
            entry.DesiredFrame = snapshot.Frames > 1 ? (clock.Frame + 1) % snapshot.Frames : 0;
            Enqueue(entry);
            return true;
        }
    }

    private bool WantsHot(Entry entry) => entry.Snapshot is { } snapshot
        && (snapshot.Length <= 2 * AudioTriggerMediaSnapshot.FrameBytes || _demand.Count <= 2);

    private bool NeedsWork(Entry entry) => entry.Active && !entry.Retired && !entry.Failed
        && (entry.Snapshot is null || (entry.Hot is null && (WantsHot(entry)
            || NextNeeded(entry) >= 0)));

    private static byte[]? FrameBuffer(Entry entry, int index) => entry.FrontIndex == index ? entry.Front
        : entry.BackIndex == index ? entry.Back : entry.AheadIndex == index ? entry.Ahead : null;

    private static int NextNeeded(Entry entry)
    {
        if (FrameBuffer(entry, entry.DesiredFrame) is null) return entry.DesiredFrame;
        var next = (entry.DesiredFrame + 1) % entry.Snapshot!.Frames;
        return FrameBuffer(entry, next) is null ? next : -1;
    }

    private void Enqueue(Entry entry)
    {
        if (entry.Queued || entry.Loading || !NeedsWork(entry)) return;
        if (_idle.Task.IsCompleted) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        entry.Queued = true;
        _queue.Enqueue(entry);
        if (_workers.Length == 0) _workers = [Task.Run(WorkerAsync), Task.Run(WorkerAsync)];
        Wake();
    }

    private Job? BeginJob(Entry entry)
    {
        if (!NeedsWork(entry)) return null;
        if (entry.Snapshot is null) return new(entry, Operation.Snapshot, null, 0);
        if (WantsHot(entry))
        {
            ReleaseWindows(entry);
            if (Reserve(entry, entry.Snapshot.Length))
            {
                _decodedBytes += entry.Snapshot.Length;
                return new(entry, Operation.Hot, new byte[entry.Snapshot.Length], 0);
            }
        }
        var index = NextNeeded(entry);
        if (index < 0) return null;
        var keep = entry.Clock.Started ? entry.Clock.Frame : -1;
        var next = (entry.DesiredFrame + 1) % entry.Snapshot.Frames;
        bool Replaceable(int candidate) => candidate < 0 || (candidate != keep && candidate != entry.DesiredFrame && candidate != next);
        if (entry.Front is null) entry.Front = RentWindow(entry);
        if (entry.Front is not null && Replaceable(entry.FrontIndex))
        {
            entry.FrontIndex = -1;
            return new(entry, Operation.Frame, entry.Front, index);
        }
        if (entry.Back is null) entry.Back = RentWindow(entry);
        if (entry.Back is not null && Replaceable(entry.BackIndex))
        {
            entry.BackIndex = -1;
            return new(entry, Operation.Frame, entry.Back, index);
        }
        if (entry.Ahead is null) entry.Ahead = RentWindow(entry);
        if (entry.Ahead is null || !Replaceable(entry.AheadIndex)) return null;
        entry.AheadIndex = -1;
        return new(entry, Operation.Frame, entry.Ahead, index);
    }

    private bool Reserve(Entry target, int bytes)
    {
        while (_decodedBytes + bytes > MaximumDecodedBytes && _spares.TryPop(out _))
        {
            _decodedBytes -= AudioTriggerMediaSnapshot.FrameBytes;
            _windowBuffers--;
        }
        while (_decodedBytes + bytes > MaximumDecodedBytes)
        {
            var victim = _entries.Values.Where(entry => entry != target && entry.Hot is not null)
                .OrderBy(entry => entry.Active).ThenBy(entry => entry.Used).FirstOrDefault();
            if (victim?.Hot is not { } hot) return false;
            victim.Hot = null;
            _decodedBytes -= hot.Length;
            victim.DesiredFrame = victim.Clock.Frame;
        }
        return true;
    }

    private byte[]? RentWindow(Entry target)
    {
        if (_spares.TryPop(out var spare)) return spare;
        if (_windowBuffers >= MaximumWindowBuffers || !Reserve(target, AudioTriggerMediaSnapshot.FrameBytes)) return null;
        _decodedBytes += AudioTriggerMediaSnapshot.FrameBytes;
        _windowBuffers++;
        return new byte[AudioTriggerMediaSnapshot.FrameBytes];
    }

    private void ReturnWindow(byte[] buffer) => _spares.Push(buffer);

    private void ReleaseWindows(Entry entry)
    {
        if (entry.Front is { } front && front != entry.InflightBuffer) ReturnWindow(front);
        if (entry.Back is { } back && back != entry.InflightBuffer) ReturnWindow(back);
        if (entry.Ahead is { } ahead && ahead != entry.InflightBuffer) ReturnWindow(ahead);
        entry.Front = entry.Back = entry.Ahead = null;
        entry.FrontIndex = entry.BackIndex = entry.AheadIndex = -1;
    }

    private async Task WorkerAsync()
    {
        var scratch = new byte[65536];
        try
        {
            while (true)
            {
                await _wake.WaitAsync(_stop.Token).ConfigureAwait(false);
                Job? job = null;
                AudioTriggerMediaSnapshot? deleting = null;
                lock (_gate)
                {
                    if (_deletes.TryDequeue(out deleting)) { _inflight++; }
                    else if (_queue.TryDequeue(out var entry))
                    {
                        entry.Queued = false;
                        job = BeginJob(entry);
                        if (job is { } accepted)
                        {
                            entry.Loading = true;
                            entry.InflightBuffer = accepted.Buffer;
                            entry.Reading = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                            _inflight++;
                        }
                    }
                }
                if (deleting is not null)
                {
                    try { _store.Value.Delete(deleting); }
                    finally { lock (_gate) _inflight--; }
                }
                if (job is { } work) await ExecuteAsync(work, scratch).ConfigureAwait(false);
                lock (_gate)
                {
                    SettleIdle();
                    if (_queue.Count != 0 || _deletes.Count != 0) Wake();
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task ExecuteAsync(Job job, byte[] scratch)
    {
        var entry = job.Entry;
        AudioTriggerMediaSnapshot? created = null;
        Exception? failure = null;
        var reading = entry.Reading!;
        try
        {
            if (job.Operation == Operation.Snapshot)
            {
                if (_beforeLoad is not null) await _beforeLoad(entry.Id, reading.Token).ConfigureAwait(false);
                reading.Token.ThrowIfCancellationRequested();
                created = await _store.Value.CreateAsync(_media, entry.Id, scratch, reading.Token).ConfigureAwait(false);
            }
            else
            {
                if (_beforeRead is not null) await _beforeRead(entry.Id, job.Index, reading.Token).ConfigureAwait(false);
                reading.Token.ThrowIfCancellationRequested();
                if (job.Operation == Operation.Hot)
                    await entry.Snapshot!.ReadAllAsync(job.Buffer!, reading.Token).ConfigureAwait(false);
                else await entry.Snapshot!.ReadFrameAsync(job.Index, job.Buffer!, reading.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (reading.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }
        lock (_gate)
        {
            entry.Loading = false;
            entry.InflightBuffer = null;
            entry.Reading = null;
            _inflight--;
            var accepted = !_disposed && !entry.Retired && !reading.IsCancellationRequested && failure is null;
            if (accepted) Publish(job, created);
            else
            {
                if (created is not null) DeleteLater(created);
                if (job.Buffer is { } buffer)
                {
                    if (job.Operation == Operation.Hot) _decodedBytes -= buffer.Length;
                    else
                    {
                        if (entry.Front == buffer) { entry.Front = null; entry.FrontIndex = -1; }
                        if (entry.Back == buffer) { entry.Back = null; entry.BackIndex = -1; }
                        if (entry.Ahead == buffer) { entry.Ahead = null; entry.AheadIndex = -1; }
                        ReturnWindow(buffer);
                    }
                }
                if (failure is not null && !entry.Retired && !_disposed)
                {
                    entry.Failed = true;
                    Console.Error.WriteLine($"[lighting-audio-trigger] media '{entry.Id}' unavailable ({failure.GetType().Name}).");
                }
            }
            if (entry.Retired)
            {
                if (entry.Snapshot is { } retired) { entry.Snapshot = null; DeleteLater(retired); }
            }
            else if (!entry.Active) ReleaseWindows(entry);
            foreach (var current in _entries.Values) Enqueue(current);
        }
        reading.Dispose();
    }

    private void Publish(Job job, AudioTriggerMediaSnapshot? created)
    {
        var entry = job.Entry;
        if (created is not null) entry.Snapshot = created;
        else if (job.Operation == Operation.Hot) entry.Hot = job.Buffer;
        else if (entry.Front == job.Buffer)
        {
            entry.Front = job.Buffer;
            entry.FrontIndex = job.Index;
        }
        else if (entry.Back == job.Buffer)
        {
            entry.Back = job.Buffer;
            entry.BackIndex = job.Index;
        }
        else { entry.Ahead = job.Buffer; entry.AheadIndex = job.Index; }
    }

    private void DeleteLater(AudioTriggerMediaSnapshot snapshot)
    {
        if (_idle.Task.IsCompleted) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _deletes.Enqueue(snapshot);
        Wake();
    }

    private void Wake()
    {
        if (_wake.CurrentCount < 2) _wake.Release();
    }

    internal void Reset()
    {
        lock (_gate) { if (!_disposed) ResetCore(); }
    }

    private void ResetCore()
    {
        _queue.Clear();
        _demand.Clear();
        foreach (var entry in _entries.Values)
        {
            entry.Retired = true;
            entry.Reading?.Cancel();
            ReleaseWindows(entry);
            if (entry.Hot is { } hot) { _decodedBytes -= hot.Length; entry.Hot = null; }
            if (!entry.Loading && entry.Snapshot is { } snapshot) DeleteLater(snapshot);
        }
        _entries.Clear();
        SettleIdle();
    }

    private void SettleIdle()
    {
        if (_queue.Count == 0 && _deletes.Count == 0 && _inflight == 0) _idle.TrySetResult();
    }

    private static TaskCompletionSource Completed()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult();
        return completion;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ResetCore();
            _stop.Cancel();
            _completion = FinishAsync();
        }
    }

    private async Task FinishAsync()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        await Task.WhenAll(_workers).ConfigureAwait(false);
        while (_deletes.TryDequeue(out var snapshot)) _store.Value.Delete(snapshot);
        if (_store.IsValueCreated) _store.Value.Dispose();
        lock (_gate)
        {
            _spares.Clear();
            _decodedBytes = _windowBuffers = 0;
        }
        _stop.Dispose();
        _wake.Dispose();
    }
}
