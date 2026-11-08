using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Serialization;

namespace Nexus.Service.Lighting;

/// <summary>One bounded, engine-owned overlay. Capture callbacks only replace the latest sample.</summary>
internal sealed class LightingAudioTriggerController : IDisposable
{
    private sealed record Configuration(LightingAudioTriggerSettings Value, string Signature, long Version);
    private sealed record Sample(AudioTriggerSample Value, long At, long Generation);
    private readonly object _renderGate = new();
    private readonly Func<LightingAudioTriggerLook, IEffect> _build;
    private readonly Action? _resetMedia;
    private readonly Dictionary<int, IEffect> _effects = new();
    private readonly HashSet<int> _failed = new();
    private readonly CanvasBuffer _canvas = new();
    private readonly CanvasBuffer _candidate = new();
    private Configuration _configuration = new(new(), "", 0);
    private Sample? _sample;
    private long _generation;
    private long _seenGeneration = -1;
    private long _seenConfiguration = -1;
    private AudioTriggerState _state = AudioTriggerDetector.Initial;
    private volatile bool _running;
    private bool _hasFrame;
    private bool _wasVisible;
    private volatile bool _disposed;

    internal LightingAudioTriggerController(Func<LightingAudioTriggerLook, IEffect> build, Action? resetMedia = null)
    {
        _build = build;
        _resetMedia = resetMedia;
    }
    internal bool Requested => !_disposed && Volatile.Read(ref _configuration).Value is { Enabled: true, Looks.Count: > 0 };
    internal bool CaptureWanted => _running && Requested;
    internal CanvasBuffer? Canvas => CaptureWanted && _state.Index is not null && _hasFrame ? _canvas : null;

    internal void Configure(LightingAudioTriggerSettings? settings)
    {
        var copy = LightingAudioTriggerPolicy.Clone(settings) ?? new();
        var signature = JsonSerializer.Serialize(copy, AppJsonContext.Default.LightingAudioTriggerSettings);
        lock (_renderGate)
        {
            if (_disposed) return;
            if (_configuration.Signature == signature) return;
            Volatile.Write(ref _configuration, new(copy, signature, _configuration.Version + 1));
            ResetDetection();
        }
    }

    internal void SetRunning(bool running)
    {
        if (_disposed) return;
        if (_running == running) return;
        _running = running;
        ResetDetection();
    }

    internal void ResetDetection()
    {
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _sample, null);
        _resetMedia?.Invoke();
    }

    internal void PublishSample(float level, float bass, float beat, long now)
    {
        if (!CaptureWanted) return;
        var generation = Volatile.Read(ref _generation);
        Volatile.Write(ref _sample, new(new(level, bass, beat), now, generation));
    }

    /// <summary>Called once per live engine tick, including paused/frozen ticks.</summary>
    internal bool Tick(bool allowed, long now, double renderTick)
    {
        lock (_renderGate)
        {
            if (_disposed) return false;
            var wasVisible = _wasVisible;
            var config = Volatile.Read(ref _configuration);
            var generation = Volatile.Read(ref _generation);
            if (_seenConfiguration != config.Version)
            {
                ReleaseEffectsCore();
                _seenConfiguration = config.Version;
            }
            if (_seenGeneration != generation)
            {
                _state = AudioTriggerDetector.Initial;
                _hasFrame = false;
                _seenGeneration = generation;
            }
            var packet = Volatile.Read(ref _sample);
            AudioTriggerSample? sample = packet is not null && packet.Generation == generation
                && now >= packet.At && now - packet.At <= AudioTriggerDetector.StaleMs ? packet.Value : null;
            _state = allowed && CaptureWanted
                ? AudioTriggerDetector.Advance(_state, config.Value, sample, now) : AudioTriggerDetector.Initial;
            if (_state.Index is { } index && !_failed.Contains(index)) Render(index, config.Value, renderTick);
            if (_state.Index is null) _hasFrame = false;
            var visible = Canvas is not null;
            var changed = visible != wasVisible;
            _wasVisible = visible;
            return changed;
        }
    }

    private void Render(int index, LightingAudioTriggerSettings settings, double tick)
    {
        try
        {
            if (!_effects.TryGetValue(index, out var effect))
            {
                effect = _build(settings.Looks[index]);
                _effects.Add(index, effect);
            }
            _candidate.Clear();
            effect.RenderFrame(_candidate, tick);
            // Keep the previous finished frame while the incoming shader compiles.
            if (effect is ShaderEffect { DrewFrame: false } or LightingAudioTriggerMediaEffect { DrewFrame: false }) return;
            _canvas.WriteFromRgb(_candidate.Pixels);
            _hasFrame = true;
        }
        catch (Exception ex)
        {
            _failed.Add(index);
            if (_effects.Remove(index, out var effect)) DisposeEffect(effect);
            Console.Error.WriteLine($"[lighting-audio-trigger] look {index + 1} failed: {ex.Message}");
        }
    }

    internal void ReleaseEffects()
    {
        lock (_renderGate) ReleaseEffectsCore();
    }

    private void ReleaseEffectsCore()
    {
        _resetMedia?.Invoke();
        foreach (var effect in _effects.Values) DisposeEffect(effect);
        _effects.Clear();
        _failed.Clear();
        _state = AudioTriggerDetector.Initial;
        _hasFrame = false;
        _wasVisible = false;
    }

    private static void DisposeEffect(IEffect effect)
    {
        try { effect.Dispose(); }
        catch (Exception ex) { Console.Error.WriteLine($"[lighting-audio-trigger] effect cleanup failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        lock (_renderGate)
        {
            if (_disposed) return;
            _disposed = true;
            _running = false;
            ReleaseEffectsCore();
        }
    }
}
