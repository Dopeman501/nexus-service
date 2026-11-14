using System.Text.Json;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Panel;

public sealed class PanelAudioTriggerBeatTransitionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "nexus-beat-transition-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly JsonConfigStore _store;
    private readonly PanelDeviceRegistry _registry;
    public PanelAudioTriggerBeatTransitionTests() { _store = new(_path); _registry = new(_store); }
    public void Dispose() { _store.Dispose(); File.Delete(_path); }

    private string Record() => _registry.Allocate("Beat transitions", new() { Surface = "lcd-wide" }).Id;
    private static PanelBackgroundTransitionsDto MainSettings(bool enabled = true, int duration = 1700) =>
        new() { Enabled = enabled, Effects = ["curtains:wave"], DurationMs = duration };
    private static void AssertBeat(PanelAudioTriggerBeatTransitionDto? beat, bool enabled, int duration)
    {
        Assert.NotNull(beat);
        Assert.Equal(enabled, beat.Enabled);
        Assert.Equal(duration, beat.DurationMs);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(800)]
    [InlineData(5000)]
    public void Valid_boundaries_roundtrip_in_both_aot_contexts_and_clone_independently(int duration)
    {
        var source = new PanelAudioTriggerBeatTransitionDto { Enabled = true, DurationMs = duration };
        Assert.True(PanelAudioTriggerBeatTransitionPolicy.IsValid(source));
        var json = JsonSerializer.Serialize(source, AppJsonContext.Default.PanelAudioTriggerBeatTransitionDto);
        var restored = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.PanelAudioTriggerBeatTransitionDto)!;
        AssertBeat(restored, true, duration);
        var clone = PanelAudioTriggerBeatTransitionPolicy.Clone(source)!;
        clone.Enabled = false;
        clone.DurationMs = 120;
        AssertBeat(source, true, duration);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(79)]
    [InlineData(5001)]
    [InlineData(int.MaxValue)]
    public void Invalid_durations_are_rejected_and_omitted_on_copy(int duration)
    {
        var invalid = new PanelAudioTriggerBeatTransitionDto { DurationMs = duration };
        Assert.False(PanelAudioTriggerBeatTransitionPolicy.IsValid(invalid));
        Assert.Null(PanelAudioTriggerBeatTransitionPolicy.Clone(invalid));
    }

    [Fact]
    public void Missing_preferences_are_not_persisted_or_materialized_by_reads()
    {
        Assert.True(PanelAudioTriggerBeatTransitionPolicy.IsValid(null));
        Assert.Null(PanelAudioTriggerBeatTransitionPolicy.Clone(null));
        AssertBeat(new PanelAudioTriggerBeatTransitionDto(), false, 800);
        var id = Record();
        Assert.Null(_registry.Get(id)!.BackgroundAudioTriggerBeatTransition);
        var preset = new PanelPreset();
        PanelPresets.Copy(_registry.Get(id)!, preset);
        Assert.Null(preset.BackgroundAudioTriggerBeatTransition);
        Assert.Null(_store.Load().PanelDevices[id].BackgroundAudioTriggerBeatTransition);
        AssertBeat(PanelAudioTriggerBeatTransitionPolicy.FromMain(preset), false, 800);
        Assert.Null(preset.BackgroundAudioTriggerBeatTransition);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Explicit_main_preference_wins_over_legacy_even_when_disabled(bool enabled)
    {
        var source = new PanelPreset { BackgroundTransitions = MainSettings(enabled),
            BackgroundMediaTransition = new() { Effect = "curtains:liquid", DurationMs = 4000 } };
        AssertBeat(PanelAudioTriggerBeatTransitionPolicy.FromMain(source), enabled, 1700);
    }

    [Fact]
    public void Legacy_migration_prefers_custom_media_then_custom_audio_and_ignores_invalid_or_default_entries()
    {
        var source = new PanelPreset {
            BackgroundTransitions = new() { Enabled = true, Effects = ["invalid"] },
            BackgroundMediaTransition = new() { Effect = "curtains:wave", DurationMs = 1200 },
            BackgroundAudioTriggerTransition = new() { Effect = "gl-transitions:WaterDrop", DurationMs = 2400 },
        };
        AssertBeat(PanelAudioTriggerBeatTransitionPolicy.FromMain(source), true, 1200);
        source.BackgroundMediaTransition.Effect = "none";
        AssertBeat(PanelAudioTriggerBeatTransitionPolicy.FromMain(source), true, 2400);
        source.BackgroundMediaTransition.Effect = "default";
        source.BackgroundAudioTriggerTransition.DurationMs = 5001;
        AssertBeat(PanelAudioTriggerBeatTransitionPolicy.FromMain(source), false, 800);
        source.BackgroundMediaTransition = new() { Effect = "curtains:unknown" };
        source.BackgroundAudioTriggerTransition = new() { Effect = "default" };
        AssertBeat(PanelAudioTriggerBeatTransitionPolicy.FromMain(source), false, 800);
    }

    [Fact]
    public void Direct_beat_patch_is_independent_and_all_readback_preset_and_profile_copies_are_isolated()
    {
        var id = Record();
        _store.Update(settings => settings.PanelDevices[id].BackgroundTransitions = MainSettings());
        var input = new PanelAudioTriggerBeatTransitionDto { Enabled = false, DurationMs = 240 };
        var saved = _registry.Patch(id, new() { BackgroundAudioTriggerBeatTransition = input })!;
        input.DurationMs = 5000;
        saved.BackgroundAudioTriggerBeatTransition!.DurationMs = 80;
        var current = _registry.Get(id)!;
        AssertBeat(current.BackgroundAudioTriggerBeatTransition, false, 240);
        Assert.Equal(1700, current.BackgroundTransitions!.DurationMs);
        Assert.Null(current.BackgroundAudioTrigger);
        var preset = new PanelPreset();
        PanelPresets.Copy(current, preset);
        preset.BackgroundAudioTriggerBeatTransition!.DurationMs = 300;
        AssertBeat(current.BackgroundAudioTriggerBeatTransition, false, 240);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(_store.Load(), PersistenceJsonContext.Default.NexusSettings);
        var restored = JsonSerializer.Deserialize(serialized, PersistenceJsonContext.Default.NexusSettings)!;
        AssertBeat(restored.PanelDevices[id].BackgroundAudioTriggerBeatTransition, false, 240);
        var exported = new NexusSettings();
        PanelProfileData.WriteTo(exported, _store.Load());
        exported.PanelDevices[id].BackgroundAudioTriggerBeatTransition!.DurationMs = 500;
        AssertBeat(_store.Load().PanelDevices[id].BackgroundAudioTriggerBeatTransition, false, 240);
        _registry.ResetToDefaults(id);
        Assert.Null(_registry.Get(id)!.BackgroundAudioTriggerBeatTransition);
        Assert.Null(_registry.Get(id)!.BackgroundTransitions);
        restored.HasPanelsData = true;
        PanelProfileData.ApplyToLive(_store.Load(), restored);
        AssertBeat(_store.Load().PanelDevices[id].BackgroundAudioTriggerBeatTransition, false, 240);
        restored.PanelDevices[id].BackgroundAudioTriggerBeatTransition!.DurationMs = 400;
        AssertBeat(_store.Load().PanelDevices[id].BackgroundAudioTriggerBeatTransition, false, 240);
        PanelProfileData.Reset(_store.Load());
        Assert.Null(_store.Load().PanelDevices[id].BackgroundAudioTriggerBeatTransition);
    }

    [Fact]
    public void Invalid_beat_import_is_omitted_independently_without_losing_main_or_normal_look()
    {
        var source = new PanelDeviceRecord { BackgroundColor = "#123456", BackgroundTransitions = MainSettings(),
            BackgroundAudioTriggerBeatTransition = new() { Enabled = true, DurationMs = 5001 } };
        var target = new PanelPreset();
        PanelPresets.Copy(source, target);
        Assert.Null(target.BackgroundAudioTriggerBeatTransition);
        Assert.Equal(1700, target.BackgroundTransitions!.DurationMs);
        Assert.Equal("#123456", target.BackgroundColor);
        var live = new NexusSettings { PanelDevices = new() { ["panel"] = new() { Id = "panel" } } };
        var file = new NexusSettings { HasPanelsData = true, PanelDevices = new() { ["panel"] = source } };
        source.Id = "panel";
        PanelProfileData.ApplyToLive(live, file);
        Assert.Null(live.PanelDevices["panel"].BackgroundAudioTriggerBeatTransition);
        Assert.Equal(1700, live.PanelDevices["panel"].BackgroundTransitions!.DurationMs);
    }

    [Fact]
    public void Invalid_beat_patch_is_atomic_and_explicit_beat_patch_wins_over_inherited_main()
    {
        var id = Record();
        _store.Update(settings => {
            settings.PanelDevices[id].BackgroundTransitions = MainSettings();
            settings.PanelDevices[id].BackgroundColor = "#123456";
        });
        Assert.Throws<ArgumentException>(() => _registry.Patch(id, new() {
            BackgroundColor = "#ffffff", BackgroundTransitions = MainSettings(false),
            BackgroundAudioTriggerBeatTransition = new() { DurationMs = 5001 } }));
        Assert.Equal("#123456", _registry.Get(id)!.BackgroundColor);
        Assert.Null(_registry.Get(id)!.BackgroundAudioTriggerBeatTransition);
        var updated = _registry.Patch(id, new() { BackgroundTransitions = MainSettings(false, 5000),
            BackgroundAudioTriggerBeatTransition = new() { Enabled = true, DurationMs = 120 } })!;
        AssertBeat(updated.BackgroundAudioTriggerBeatTransition, true, 120);
        Assert.False(updated.BackgroundTransitions!.Enabled);
    }

    [Fact]
    public void First_main_edit_freezes_legacy_values_before_replacing_or_editing_other_transition_fields()
    {
        var id = Record();
        _store.Update(settings => {
            settings.PanelDevices[id].BackgroundMediaTransition = new() { Effect = "curtains:wave", DurationMs = 1600 };
            settings.PanelDevices[id].BackgroundAudioTriggerTransition = new() { Effect = "curtains:liquid", DurationMs = 900 };
        });
        var saved = _registry.Patch(id, new() { BackgroundTransitions = MainSettings(false, 5000),
            BackgroundMediaTransition = new() { Effect = "none", DurationMs = 80 } })!;
        AssertBeat(saved.BackgroundAudioTriggerBeatTransition, true, 1600);
        var next = _registry.Patch(id, new() { BackgroundTransitions = MainSettings(true, 80) })!;
        AssertBeat(next.BackgroundAudioTriggerBeatTransition, true, 1600);
    }

    [Fact]
    public void Universal_audio_target_controls_preserve_both_transition_preferences()
    {
        var settings = new NexusSettings { PanelDevices = new() { ["panel"] = new() {
            Id = "panel", BackgroundTransitions = MainSettings(false),
            BackgroundAudioTriggerBeatTransition = new() { Enabled = true, DurationMs = 240 },
            BackgroundAudioTrigger = new() { Enabled = true, Backgrounds = [new() { Mode = "solid", Color = "#123456" }] },
        } } };
        Assert.True(AudioTriggerTargets.Apply(settings, [], new() { TargetIds = ["background:panel"], Enabled = false, Intensity = 25 }));
        var record = settings.PanelDevices["panel"];
        Assert.False(record.BackgroundAudioTrigger!.Enabled);
        Assert.Equal(25, record.BackgroundAudioTrigger.Intensity);
        Assert.False(record.BackgroundTransitions!.Enabled);
        Assert.Equal(1700, record.BackgroundTransitions.DurationMs);
        AssertBeat(record.BackgroundAudioTriggerBeatTransition, true, 240);
    }
}
