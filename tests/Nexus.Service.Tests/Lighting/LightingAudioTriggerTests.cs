using System.Text.Json;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Lighting;

public sealed class LightingAudioTriggerTests
{
    internal static LightingAudioTriggerSettings Settings(double delay = 1) => new()
    {
        Enabled = true, SwitchDelay = delay,
        Looks = [new() { Mode = "static", Color = "#ff0000" }, new() { Mode = "static", Color = "#0000ff" }],
    };

    [Fact]
    public void Sustained_audio_enters_after_delay_distinct_attacks_advance_and_silence_restores()
    {
        var settings = Settings();
        var state = AudioTriggerDetector.Initial;
        var sound = new AudioTriggerSample(0.8f, 0.8f, 0.8f);
        state = AudioTriggerDetector.Advance(state, settings, sound, 0);
        Assert.Null(state.Index);
        state = AudioTriggerDetector.Advance(state, settings, sound, 999);
        Assert.Null(state.Index);
        state = AudioTriggerDetector.Advance(state, settings, sound, 1000);
        Assert.Equal(0, state.Index);
        state = AudioTriggerDetector.Advance(state, settings, sound, 1200);
        Assert.Equal(0, state.Index);
        state = AudioTriggerDetector.Advance(state, settings, new(0.8f, 0, 0), 1250);
        state = AudioTriggerDetector.Advance(state, settings, sound, 1300);
        Assert.Equal(1, state.Index);
        state = AudioTriggerDetector.Advance(state, settings, null, 1400);
        Assert.Equal(1, state.Index);
        state = AudioTriggerDetector.Advance(state, settings, null, 2400);
        Assert.Null(state.Index);
    }

    [Fact]
    public void Pings_cancel_entry_and_shuffle_never_repeats_the_same_look()
    {
        var settings = Settings();
        var state = AudioTriggerDetector.Advance(AudioTriggerDetector.Initial, settings, new(1, 1, 1), 0);
        state = AudioTriggerDetector.Advance(state, settings, null, 400);
        state = AudioTriggerDetector.Advance(state, settings, new(1, 1, 1), 1500);
        Assert.Null(state.Index);
        settings.SwitchDelay = 0;
        settings.Shuffle = true;
        state = AudioTriggerDetector.Advance(state, settings, new(1, 1, 1), 1501, () => 0.99);
        var previous = state.Index;
        state = AudioTriggerDetector.Advance(state, settings, new(1, 0, 0), 1700);
        state = AudioTriggerDetector.Advance(state, settings, new(1, 1, 1), 1750, () => 0.99);
        Assert.NotEqual(previous, state.Index);
        settings.Enabled = false;
        Assert.Null(AudioTriggerDetector.Advance(state, settings, new(1, 1, 1), 1800).Index);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(101, 1)]
    [InlineData(50, 11)]
    public void Invalid_bounds_are_rejected(double intensity, double delay)
    {
        var settings = Settings(delay);
        settings.Intensity = intensity;
        Assert.False(LightingAudioTriggerPolicy.IsValid(settings));
    }

    [Fact]
    public void Policy_rejects_bad_looks_and_deep_clones_valid_shader_snapshots()
    {
        var settings = Settings();
        settings.Looks.Add(new() { Mode = "animate", Effect = "plasma", State = new() { Params = new() { ["u_background_zoom"] = 2 } } });
        var clone = LightingAudioTriggerPolicy.Clone(settings)!;
        settings.Looks[2].State!.Params["u_background_zoom"] = 3;
        Assert.Equal(2, clone.Looks[2].State!.Params["u_background_zoom"]);
        settings.Looks[0].Color = "url(external)";
        Assert.False(LightingAudioTriggerPolicy.IsValid(settings));
        Assert.Null(LightingAudioTriggerPolicy.Clone(settings));
        settings = Settings();
        settings.Looks.Add(new() { Mode = "animate", Effect = "custom:untrusted" });
        Assert.False(LightingAudioTriggerPolicy.IsValid(settings));
    }

    [Fact]
    public void Settings_round_trip_and_preset_copies_are_independent()
    {
        var settings = new NexusSettings();
        settings.Lighting.AudioTrigger = Settings();
        var json = JsonSerializer.Serialize(settings, PersistenceJsonContext.Default.NexusSettings);
        var copy = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.NexusSettings)!;
        Assert.Equal("#ff0000", copy.Lighting.AudioTrigger!.Looks[0].Color);
        var preset = LightingPresetLooks.Capture(settings.Lighting);
        settings.Lighting.AudioTrigger.Looks[0].Color = "#ffffff";
        LightingPresetLooks.Apply(settings.Lighting, preset);
        Assert.Equal("#ff0000", settings.Lighting.AudioTrigger.Looks[0].Color);
        Assert.NotSame(preset.AudioTrigger, settings.Lighting.AudioTrigger);
    }

    [Fact]
    public void New_disabled_preset_restores_off_while_legacy_preset_leaves_trigger_unchanged()
    {
        var lighting = new LightingSettings();
        var disabled = LightingPresetLooks.Capture(lighting);
        lighting.AudioTrigger = Settings();
        LightingPresetLooks.Apply(lighting, new LightingPresetLook());
        Assert.True(lighting.AudioTrigger.Enabled);
        LightingPresetLooks.Apply(lighting, disabled);
        Assert.False(lighting.AudioTrigger!.Enabled);
    }

    [Fact]
    public void Policy_bounds_collection_uniforms_and_shader_state_before_cloning()
    {
        Assert.True(LightingAudioTriggerPolicy.IsValid(null));
        var settings = Settings();
        settings.Looks = Enumerable.Range(0, 33).Select(_ => new LightingAudioTriggerLook { Color = "#123456" }).ToList();
        Assert.False(LightingAudioTriggerPolicy.IsValid(settings));
        settings = Settings();
        settings.Looks = [new() { Mode = "animate", Effect = "plasma", State = new() { Params = new() { ["bad-name"] = 1 } } }];
        Assert.False(LightingAudioTriggerPolicy.IsValid(settings));
        settings.Looks[0].State!.Params = new() { ["u_zoom"] = float.NaN };
        Assert.False(LightingAudioTriggerPolicy.IsValid(settings));
        settings.Looks[0].State!.Params = new();
        settings.Looks[0].State!.Speed = 101;
        Assert.False(LightingAudioTriggerPolicy.IsValid(settings));
        settings.Looks[0].State!.Speed = 50;
        settings.Looks[0].State!.Sensors = new();
        Assert.False(LightingAudioTriggerPolicy.IsValid(settings));
    }

    [Fact]
    public void Sensitivity_invalid_samples_single_look_and_refractory_interval_are_bounded()
    {
        var settings = Settings(0);
        settings.Intensity = 0;
        Assert.Null(AudioTriggerDetector.Advance(AudioTriggerDetector.Initial, settings, new(0.05f, 0.1f, 0.1f), 0).Index);
        settings.Intensity = 100;
        var state = AudioTriggerDetector.Advance(AudioTriggerDetector.Initial, settings, new(0.05f, 0.1f, 0.1f), 0);
        Assert.Equal(0, state.Index);
        state = AudioTriggerDetector.Advance(state, settings, new(1, 0, 0), 10);
        state = AudioTriggerDetector.Advance(state, settings, new(1, 1, 1), 20);
        Assert.Equal(0, state.Index);
        Assert.Null(AudioTriggerDetector.Advance(state, settings, new(float.NaN, 0, 0), 30).Index);
        settings.Looks.RemoveAt(1);
        state = AudioTriggerDetector.Advance(state, settings, new(1, 0, 0), 200);
        state = AudioTriggerDetector.Advance(state, settings, new(1, 1, 1), 300);
        Assert.Equal(0, state.Index);
        settings.Looks.Clear();
        Assert.Null(AudioTriggerDetector.Advance(state, settings, new(1, 1, 1), 400).Index);
    }
}
