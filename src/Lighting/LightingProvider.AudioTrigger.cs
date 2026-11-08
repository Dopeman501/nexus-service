using System;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Effects;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Models.Lighting;

namespace Nexus.Service.Lighting;

public sealed partial class LightingProvider
{
    private LightingAudioTriggerController? _audioTrigger;
    private LightingAudioTriggerMediaResources? _audioTriggerMedia;
    private string _audioTriggerSource = "";
    private readonly object _audioTriggerHydrateGate = new();
    private bool _audioTriggerDisposed;

    private void InitializeAudioTrigger()
    {
        _audioTriggerMedia = new LightingAudioTriggerMediaResources(_media);
        _audioTrigger = new LightingAudioTriggerController(BuildAudioTriggerLook, _audioTriggerMedia.Reset);
        _engine.AudioTrigger = _audioTrigger;
        _store.OnChanged += RefreshAudioTrigger;
        if (_beats is { } beats)
        {
            beats.OnBeat += SampleAudioTrigger;
        }
        RefreshAudioTrigger();
    }

    internal IEffect BuildAudioTriggerLook(LightingAudioTriggerLook look)
    {
        if (look.Mode == "media")
        {
            return new LightingAudioTriggerMediaEffect(_audioTriggerMedia!, look.MediaId!);
        }
        if (StaticColorHex.TryParse(look.Color, out var r, out var g, out var b))
        {
            return new FlatColorEffect(r, g, b);
        }
        var state = look.State!;
        var effect = BuildAnimateEffect(look.Effect!, look.Mode == "static" ? 0 : state.Speed / 50f,
            state.Intensity, state.Hue, state.Colorize, state.Saturation, state.Contrast,
            new System.Collections.Generic.Dictionary<string, float>(state.Params));
        // This one base uniform has no generic default push. Keep explicit snapshot values,
        // including zero, while an omitted boost uses the audio effect's authored default.
        if (effect is ShaderEffect shader && !state.Params.ContainsKey("u_audioBoost")
            && ShaderLibrary.Params(look.Effect!).TryGetValue("u_audioBoost", out var boost))
        {
            shader.AudioBoost = boost.Default;
        }
        return effect;
    }

    private void RefreshAudioTrigger()
    {
        lock (_audioTriggerHydrateGate)
        {
            if (_audioTriggerDisposed)
            {
                return;
            }
            LightingAudioTriggerSettings? settings = null;
            var source = "";
            _store.Read(document =>
            {
                var lighting = document.Lighting;
                settings = LightingAudioTriggerPolicy.Clone(lighting.AudioTrigger);
                source = lighting.AudioOutputDeviceDirection + ":" + lighting.AudioOutputDeviceId;
            });
            _audioTrigger?.Configure(settings);
            if (_audioTriggerSource != source)
            {
                _audioTriggerSource = source;
                _audioTrigger?.ResetDetection();
            }
            ReconcileAudioCapture();
        }
    }

    private void SampleAudioTrigger() =>
        _audioTrigger?.PublishSample(AudioState.Level, AudioState.Bass, AudioState.Beat, Environment.TickCount64);

    private void DisposeAudioTrigger()
    {
        lock (_audioTriggerHydrateGate)
        {
            if (_audioTriggerDisposed)
            {
                return;
            }
            _audioTriggerDisposed = true;
            _store.OnChanged -= RefreshAudioTrigger;
            if (_beats is { } beats)
            {
                beats.OnBeat -= SampleAudioTrigger;
            }
            _audioTrigger?.SetRunning(false);
            _audioTrigger?.Dispose();
            _audioTriggerMedia?.Dispose();
            _engine.AudioTrigger = null;
        }
    }
}
