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
    private sealed record Configuration(LightingAudioTriggerSettings Value, string Signature, string ResourceSignature, long Version);
    private sealed record Sample(AudioTriggerSample Value, long At, long Generation);
    private sealed class Detection
    {
        internal AudioTriggerState State = AudioTriggerDetector.Initial;
        internal CanvasBuffer? Canvas;
    }
    private readonly object _renderGate = new();
    private readonly Func<LightingAudioTriggerLook, IEffect> _build;
    private readonly Action? _resetMedia;
    private readonly Dictionary<int, IEffect> _effects = new();
    private readonly HashSet<int> _failed = new();
    private readonly Dictionary<int, CanvasBuffer> _frames = new();
    private readonly Dictionary<double, Detection> _detections = new();
    private readonly HashSet<int> _rendered = new();
    private readonly CanvasBuffer _candidate = new();
    private Configuration _configuration = new(new(), "", "", 0);
    private Sample? _sample;
    private long _generation;
    private long _seenGeneration = -1;
    private long _seenConfiguration = -1;
    private string _seenResources = "";
    private volatile bool _running;
    private bool _anyVisible;
    private bool _wasVisible;
    private volatile bool _disposed;

    internal LightingAudioTriggerController(Func<LightingAudioTriggerLook, IEffect> build, Action? resetMedia = null)
    {
        _build = build;
        _resetMedia = resetMedia;
    }
    internal bool Requested => !_disposed && Volatile.Read(ref _configuration).Value is { Enabled: true, Looks.Count: > 0 };
    internal bool CaptureWanted => _running && Requested;
    internal bool AnyCanvas => CaptureWanted && _anyVisible;
    internal CanvasBuffer? Canvas => CanvasFor(null);

    internal CanvasBuffer? CanvasFor(string? deviceId)
    {
        if (!CaptureWanted) return null;
        var settings = Volatile.Read(ref _configuration).Value;
        var control = deviceId is null ? null : settings.DeviceOverrides?.GetValueOrDefault(deviceId);
        if (control?.Enabled == false) return null;
        return _detections.GetValueOrDefault(control?.Intensity ?? settings.Intensity)?.Canvas;
    }

    internal void Configure(LightingAudioTriggerSettings? settings)
    {
        var copy = LightingAudioTriggerPolicy.Clone(settings) ?? new();
        var signature = JsonSerializer.Serialize(copy, AppJsonContext.Default.LightingAudioTriggerSettings);
        var resources = JsonSerializer.Serialize(new LightingAudioTriggerSettings
        {
            Looks = copy.Looks, SwitchDelay = copy.SwitchDelay, Shuffle = copy.Shuffle,
        }, AppJsonContext.Default.LightingAudioTriggerSettings);
        lock (_renderGate)
        {
            if (_disposed) return;
            if (_configuration.Signature == signature) return;
            var reset = _configuration.ResourceSignature != resources || _configuration.Value.Enabled != copy.Enabled;
            Volatile.Write(ref _configuration, new(copy, signature, resources, _configuration.Version + 1));
            if (reset) ResetDetection();
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
            var configurationChanged = _seenConfiguration != config.Version;
            if (_seenConfiguration != config.Version)
            {
                _seenConfiguration = config.Version;
            }
            if (_seenResources != config.ResourceSignature)
            {
                ReleaseEffectsCore();
                _seenResources = config.ResourceSignature;
            }
            if (_seenGeneration != generation)
            {
                _detections.Clear();
                _anyVisible = false;
                _seenGeneration = generation;
            }
            var packet = Volatile.Read(ref _sample);
            AudioTriggerSample? sample = packet is not null && packet.Generation == generation
                && now >= packet.At && now - packet.At <= AudioTriggerDetector.StaleMs ? packet.Value : null;
            _rendered.Clear();
            _advanced.Clear();
            Advance(config.Value.Intensity);
            if (config.Value.DeviceOverrides is { } overrides)
            {
                foreach (var control in overrides.Values)
                {
                    if (control.Enabled != false && control.Intensity is { } intensity && intensity != config.Value.Intensity)
                        Advance(intensity);
                }
            }
            foreach (var old in _detections.Keys.Where(intensity => !_advanced.Contains(intensity)).ToArray()) _detections.Remove(old);
            _anyVisible = allowed && CaptureWanted && _detections.Values.Any(detection => detection.Canvas is not null);
            var visible = AnyCanvas;
            var changed = visible != wasVisible || configurationChanged;
            _wasVisible = visible;
            return changed;

            void Advance(double intensity)
            {
                if (!_detections.TryGetValue(intensity, out var detection))
                    _detections[intensity] = detection = new();
                // Multiple devices sharing a sensitivity share one detector and one rendered look.
                if (!_advanced.Add(intensity)) return;
                detection.State = allowed && CaptureWanted
                    ? AudioTriggerDetector.Advance(detection.State, config.Value, sample, now, intensity: intensity)
                    : AudioTriggerDetector.Initial;
                if (detection.State.Index is not { } index) { detection.Canvas = null; return; }
                if (!_failed.Contains(index) && _rendered.Add(index)) Render(index, config.Value, renderTick);
                if (_frames.TryGetValue(index, out var frame)) detection.Canvas = frame;
            }
        }
    }

    private readonly HashSet<double> _advanced = new();

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
            if (!_frames.TryGetValue(index, out var frame)) _frames[index] = frame = new();
            frame.WriteFromRgb(_candidate.Pixels);
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
        _frames.Clear();
        _detections.Clear();
        _anyVisible = false;
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
