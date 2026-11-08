using Nexus.Service.Activity;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Effects;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Media;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
using Nexus.Service.Models.Lighting;

namespace Nexus.Service.Tests.Lighting;

public sealed class LightingAudioTriggerCaptureTests : IDisposable
{
    private sealed class FakeBeats : IBeatsProvider
    {
        public bool Running { get; private set; }
        public event Action? OnBeat;
        public void Start() => Running = true;
        public void Stop() => Running = false;
        public void Emit(float level, float bass, float beat)
        {
            AudioState.Level = level;
            AudioState.Bass = bass;
            AudioState.Beat = beat;
            OnBeat?.Invoke();
        }
        public void Dispose() { }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-lighting-trigger-" + Guid.NewGuid().ToString("N"));
    private readonly JsonConfigStore _store;
    private readonly LightingEngine _engine = new() { FrameIntervalMs = 5 };
    private readonly GpuContext _gpu = new(160, 90);
    private readonly FakeBeats _beats = new();
    private readonly LightingProvider _provider;

    public LightingAudioTriggerCaptureTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new JsonConfigStore(Path.Combine(_dir, "settings.json"));
        _provider = new LightingProvider(_store, _engine, new LightingOutputHub(), _gpu, new MediaLibrary(Path.Combine(_dir, "media")),
            new Nexus.Service.Platform.DefaultMonitorEnumerator(), beats: _beats);
    }

    [Fact]
    public async Task Native_trigger_captures_without_UI_or_MusicReactive_and_never_persists_rhythm_steps()
    {
        _store.Update(settings =>
        {
            settings.Lighting.MusicReactive = false;
            settings.Lighting.AudioTrigger = LightingAudioTriggerTests.Settings(0);
        });
        Assert.False(_beats.Running);
        var device = new DeviceFrame(0, "fake-led", 1, 450, 250, 100, 100);
        _engine.UpdateDevices([device]);
        _engine.SetEffect(new FlatColorEffect(0, 255, 0));
        Assert.True(_beats.Running);
        var changes = 0;
        _store.OnChanged += () => changes++;
        _beats.Emit(1, 0, 0);
        await WaitFor(device, [255, 0, 0]);
        _beats.Emit(1, 0, 0);
        _beats.Emit(1, 1, 1);
        // Distinct beats are covered with deterministic time in detector/controller tests.
        Assert.Equal(0, changes);
        Assert.False(_store.Load().Lighting.MusicReactive);
        Assert.Equal("flat", _engine.CurrentEffectName);
        _provider.StopAll();
        Assert.False(_beats.Running);
        Assert.Null(_engine.AudioTrigger!.Canvas);
    }

    [Fact]
    public void Disable_preserves_other_audio_consumers_and_dispose_does_not_resurrect_capture()
    {
        _engine.SetEffect(new FlatColorEffect(0, 255, 0));
        _store.Update(settings => settings.Lighting.AudioTrigger = LightingAudioTriggerTests.Settings(0));
        Assert.True(_beats.Running);
        _provider.SetAudioCaptureDemand(true);
        _store.Update(settings => settings.Lighting.AudioTrigger!.Enabled = false);
        Assert.True(_beats.Running);
        _provider.SetAudioCaptureDemand(false);
        Assert.False(_beats.Running);
        _provider.Dispose();
        _provider.ReconcileAudioCapture();
        Assert.False(_beats.Running);
    }

    [Fact]
    public void Audio_effect_uses_authored_boost_without_flipping_MusicReactive_and_respects_saved_zero()
    {
        _store.Update(settings => settings.Lighting.MusicReactive = false);
        using var effect = (ShaderEffect)_provider.BuildAudioTriggerLook(new LightingAudioTriggerLook
        {
            Mode = "animate", Effect = "scope", State = new(),
        });
        Assert.Equal(1, effect.AudioBoost);
        Assert.False(_store.Load().Lighting.MusicReactive);
        using var muted = (ShaderEffect)_provider.BuildAudioTriggerLook(new LightingAudioTriggerLook
        {
            Mode = "animate", Effect = "scope", State = new() { Params = new() { ["u_audioBoost"] = 0 } },
        });
        Assert.Equal(0, muted.ExtraParams!["u_audioBoost"]);
    }

    [Fact]
    public async Task Media_trigger_renders_unfiltered_private_frames_without_mutating_shared_media_settings()
    {
        var library = new MediaLibrary(Path.Combine(_dir, "media"));
        Directory.CreateDirectory(library.GetItemDir("clip"));
        File.WriteAllText(Path.Combine(library.GetItemDir("clip"), "meta.json"),
            """{"id":"clip","width":160,"height":90,"frames":1,"fps":0}""");
        var frames = new byte[160 * 90 * 3];
        for (var index = 0; index < frames.Length; index += 3) frames[index] = 255;
        File.WriteAllBytes(library.GetFramesBinPath("clip"), frames);
        _provider.UpdateMediaEffect(0, 0, 0, 1, false, false, persist: true); // normal Media is greyscale
        _store.Update(settings =>
        {
            settings.Lighting.LastMediaId = "normal-clip";
            settings.Lighting.AudioTrigger = new() { Enabled = true, SwitchDelay = 0,
                Looks = [new() { Mode = "media", MediaId = "clip" }] };
        });
        var sync = _store.Load().Lighting.Sync;
        var changes = 0;
        _store.OnChanged += () => changes++;
        var device = new DeviceFrame(0, "fake-led", 1, 450, 250, 100, 100);
        _engine.UpdateDevices([device]);
        void Audio() => _beats.Emit(1, 0, 0);
        _engine.FramePublished += Audio;
        try
        {
            _engine.SetEffect(new FlatColorEffect(0, 255, 0));
            await WaitFor(device, [255, 0, 0]);
        }
        finally { _engine.FramePublished -= Audio; }
        Assert.Equal(0, changes);
        Assert.Equal(sync, _store.Load().Lighting.Sync);
        Assert.Equal("normal-clip", _store.Load().Lighting.LastMediaId);
        Assert.Equal(0, _store.Load().Lighting.MediaEffect.Saturation);
        Assert.Equal("flat", _engine.CurrentEffectName);
    }

    private async Task WaitFor(DeviceFrame device, byte[] expected)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe() { if (device.LedBytes.SequenceEqual(expected)) done.TrySetResult(); }
        _engine.FramePublished += Observe;
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { _engine.FramePublished -= Observe; }
    }

    public void Dispose()
    {
        _provider.Dispose();
        _gpu.Dispose();
        _store.Dispose();
        AudioState.Reset();
        Directory.Delete(_dir, recursive: true);
    }
}
