using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.JungleLeopard;

/// <summary>Adapts the synchronous coordinator contract to one identified D215 session's serial pump.</summary>
internal sealed class D215StreamTransport : IStreamedPanelTransport, IRepeatGatedPanelTransport
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(6);
    private readonly object _lock = new();
    private readonly Func<string, D215Session?> _lookup;
    private readonly Func<Func<bool>, D215BgraFrames> _makeFrames;
    private readonly TimeSpan _startTimeout;
    private Func<bool>? _repeatAllowed;
    private Binding? _binding;
    private bool _disposed;

    internal D215StreamTransport(string serial, D215ConnectionWorker worker, bool rotateLandscape = false)
        : this(serial, worker.GetIdentifiedSession, mayRepeat => D215BgraFrames.Create(mayRepeat, rotateLandscape), StartTimeout)
    {
    }

    internal D215StreamTransport(string serial, Func<string, D215Session?> lookup,
        Func<Func<bool>, D215BgraFrames> makeFrames, TimeSpan startTimeout)
    {
        Serial = serial;
        _lookup = lookup;
        _makeFrames = makeFrames;
        _startTimeout = startTimeout;
    }

    public string Serial { get; }

    public bool IsOpen
    {
        get
        {
            var binding = Volatile.Read(ref _binding);
            return binding?.Session.CanStream == true && binding.Stream?.Completion.IsCompleted != true;
        }
    }

    public void BindRepeatGate(Func<bool> allowed) => Volatile.Write(ref _repeatAllowed, allowed);

    public void Open()
    {
        var session = _lookup(Serial);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_binding is not null) throw new InvalidOperationException("The D215 stream transport is already open.");
            if (session?.CanStream != true) throw new IOException("No identified D215 session is available for streaming.");
            var frames = _makeFrames(() => Volatile.Read(ref _repeatAllowed)?.Invoke() == true);
            Volatile.Write(ref _binding, new Binding(session, frames));
        }
    }

    public void StartPlayer()
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var session = Volatile.Read(ref _binding)?.Session;
            if (session is null || !ReferenceEquals(_lookup(Serial), session))
                throw new IOException("The identified D215 session was lost before playback started.");

            // A replacement on the same lease must wait for the old pump's final clear.
            if (session.GetRetiringStreamCompletion() is { } retiring)
                retiring.WaitAsync(RemainingStartTime(startedAt)).GetAwaiter().GetResult();

            D215LiveStream stream;
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (RemainingStartTime(startedAt) == TimeSpan.Zero)
                    throw new TimeoutException("The D215 player did not start within the startup budget.");
                var binding = _binding!;
                stream = binding.Stream ?? session.BeginStreaming(binding.Frames, () => ReferenceEquals(_lookup(Serial), session));
                Volatile.Write(ref _binding, binding with { Stream = stream });
            }

            // The coordinator opens transports off its lock and expects a successful start
            // to mean that the device acknowledged playback.
            try { stream.Started.WaitAsync(RemainingStartTime(startedAt)).GetAwaiter().GetResult(); }
            catch (TimeoutException ex)
            {
                stream.Fail(ex);
                throw;
            }
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
            if (!ReferenceEquals(_lookup(Serial), session) || !IsOpen)
                throw new IOException("The D215 session ended during playback startup.", stream.Failure);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private TimeSpan RemainingStartTime(long startedAt)
    {
        var remaining = _startTimeout - Stopwatch.GetElapsedTime(startedAt);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    public void Write(ReadOnlySpan<byte> payload)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        var binding = Volatile.Read(ref _binding);
        if (binding?.Session.CanStream != true || binding.Stream is not { } stream
            || !stream.Started.IsCompletedSuccessfully || stream.Completion.IsCompleted)
        {
            throw new IOException("The D215 player is unavailable.");
        }

        binding.Frames.Queue(payload);
    }

    public void Dispose()
    {
        Binding? binding;
        lock (_lock)
        {
            if (_disposed) return;
            Volatile.Write(ref _disposed, true);
            binding = _binding;
            Volatile.Write(ref _binding, null);
        }

        if (binding?.Stream is { } stream)
        {
            stream.RequestStop();
            _ = DisposeFramesAfterStreamAsync(stream, binding.Frames);
        }
        else
        {
            binding?.Frames.Dispose();
        }
    }

    private static async Task DisposeFramesAfterStreamAsync(D215LiveStream stream, D215BgraFrames frames)
    {
        try { await stream.Completion.ConfigureAwait(false); }
        catch (Exception ex) { ServiceLog.Warn($"[jungle-leopard-d215] stream ended during cleanup ({ex.GetType().Name})"); }
        try { frames.Dispose(); }
        catch (Exception ex) { ServiceLog.Error($"[jungle-leopard-d215] encoder cleanup failed ({ex.GetType().Name})"); }
    }

    private sealed record Binding(D215Session Session, D215BgraFrames Frames, D215LiveStream? Stream = null);
}
