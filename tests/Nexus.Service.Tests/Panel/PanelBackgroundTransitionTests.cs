using System.Text.Json;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Panel;

public sealed class PanelBackgroundTransitionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "nexus-background-transition-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly JsonConfigStore _store;
    private readonly PanelDeviceRegistry _registry;

    public PanelBackgroundTransitionTests()
    {
        _store = new JsonConfigStore(_path);
        _registry = new PanelDeviceRegistry(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        File.Delete(_path);
    }

    private string Record() => _registry.Allocate("Transitions", new() { Surface = "lcd-wide" }).Id;

    private static PanelDevicePatch Patch(string json) => JsonSerializer.Deserialize(json, AppJsonContext.Default.PanelDevicePatch)!;

    [Fact]
    public void Missing_settings_stay_absent_and_explicit_defaults_reset_only_the_requested_transition()
    {
        var id = Record();
        var empty = JsonSerializer.SerializeToElement(_registry.Get(id)!, AppJsonContext.Default.PanelDeviceRecord);
        Assert.False(empty.TryGetProperty("backgroundMediaTransition", out _));
        Assert.False(empty.TryGetProperty("backgroundAudioTriggerTransition", out _));
        _registry.Patch(id, Patch("""
            {"backgroundMediaTransition":{"effect":"gl-transitions:CrossZoom","durationMs":950},
             "backgroundAudioTriggerTransition":{"effect":"curtains:liquid","durationMs":120}}
            """));
        var defaults = _registry.Patch(id, Patch("""{"backgroundMediaTransition":{}}"""))!;
        AssertTransition(defaults.BackgroundMediaTransition, "default", 800);
        AssertTransition(defaults.BackgroundAudioTriggerTransition, "curtains:liquid", 120);
        var omitted = _registry.Patch(id, Patch("""{"backgroundMediaTransition":null,"accentColor":"#ffffff"}"""))!;
        AssertTransition(omitted.BackgroundAudioTriggerTransition, "curtains:liquid", 120);
        AssertTransition(omitted.BackgroundMediaTransition, "default", 800);
    }

    [Fact]
    public void Independent_patches_preserve_audio_configuration_and_normal_slideshow_fields()
    {
        var id = Record();
        _registry.Patch(id, Patch("""
            {"backgroundMode":"media","backgroundMediaId":"normal","backgroundMediaType":"static",
             "backgroundMediaSlideshow":true,"backgroundMediaInterval":15,"backgroundMediaOrder":["normal","second"],
             "backgroundAudioTrigger":{"enabled":true,"intensity":62,"backgrounds":[{"mode":"solid","color":"#abcdef"}]},
             "backgroundAudioTriggerTransition":{"effect":"curtains:ripple","durationMs":180}}
            """));
        var updated = _registry.Patch(id, Patch("""
            {"backgroundMediaTransition":{"effect":"gl-transitions:CrossZoom","durationMs":5000}}
            """))!;
        Assert.Equal("media", updated.BackgroundMode);
        Assert.Equal("normal", updated.BackgroundMediaId);
        Assert.True(updated.BackgroundMediaSlideshow);
        Assert.Equal(15, updated.BackgroundMediaInterval);
        Assert.Equal(new[] { "normal", "second" }, updated.BackgroundMediaOrder);
        Assert.Equal(62, updated.BackgroundAudioTrigger!.Intensity);
        AssertTransition(updated.BackgroundAudioTriggerTransition, "curtains:ripple", 180);
        AssertTransition(updated.BackgroundMediaTransition, "gl-transitions:CrossZoom", 5000);
    }

    [Fact]
    public void Patch_readback_and_preset_snapshots_have_independent_transition_objects()
    {
        var id = Record();
        var shared = new PanelBackgroundTransitionDto { Effect = "curtains:wave", DurationMs = 180 };
        var patch = new PanelDevicePatch { BackgroundMediaTransition = shared, BackgroundAudioTriggerTransition = shared };
        var saved = _registry.Patch(id, patch)!;
        shared.Effect = "none";
        saved.BackgroundMediaTransition!.DurationMs = 5000;
        saved.BackgroundAudioTriggerTransition!.Effect = "default";
        var record = _registry.Get(id)!;
        AssertTransition(record.BackgroundMediaTransition, "curtains:wave", 180);
        AssertTransition(record.BackgroundAudioTriggerTransition, "curtains:wave", 180);
        Assert.NotSame(record.BackgroundMediaTransition, record.BackgroundAudioTriggerTransition);
        var preset = new PanelPreset();
        PanelPresets.Copy(record, preset);
        preset.BackgroundMediaTransition!.Effect = "none";
        preset.BackgroundAudioTriggerTransition!.DurationMs = 5000;
        AssertTransition(record.BackgroundMediaTransition, "curtains:wave", 180);
        AssertTransition(record.BackgroundAudioTriggerTransition, "curtains:wave", 180);
    }

    [Fact]
    public void Persisted_profiles_and_presets_roundtrip_both_fields_and_resets_clear_them()
    {
        var id = Record();
        var saved = _registry.Patch(id, Patch("""
            {"backgroundColor":"#123456","backgroundMediaTransition":{"effect":"gl-transitions:CrossZoom","durationMs":950},
             "backgroundAudioTriggerTransition":{"effect":"curtains:liquid","durationMs":120}}
            """))!;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(_store.Load(), PersistenceJsonContext.Default.NexusSettings);
        var restored = JsonSerializer.Deserialize(bytes, PersistenceJsonContext.Default.NexusSettings)!.PanelDevices[id];
        AssertTransition(restored.BackgroundMediaTransition, "gl-transitions:CrossZoom", 950);
        AssertTransition(restored.BackgroundAudioTriggerTransition, "curtains:liquid", 120);
        var preset = new PanelPreset();
        PanelPresets.Copy(saved, preset);
        var presetJson = JsonSerializer.SerializeToUtf8Bytes(preset, PersistenceJsonContext.Default.PanelPreset);
        var restoredPreset = JsonSerializer.Deserialize(presetJson, PersistenceJsonContext.Default.PanelPreset)!;
        AssertTransition(restoredPreset.BackgroundMediaTransition, "gl-transitions:CrossZoom", 950);
        AssertTransition(restoredPreset.BackgroundAudioTriggerTransition, "curtains:liquid", 120);
        Assert.False(PanelProfileData.IsDefaultLook(saved));
        var exported = new NexusSettings();
        PanelProfileData.WriteTo(exported, _store.Load());
        _registry.ResetToDefaults(id);
        PanelProfileData.ApplyToLive(_store.Load(), exported);
        AssertTransition(_store.Load().PanelDevices[id].BackgroundAudioTriggerTransition, "curtains:liquid", 120);
        var reset = _registry.ResetToDefaults(id)!;
        Assert.Null(reset.BackgroundMediaTransition);
        Assert.Null(reset.BackgroundAudioTriggerTransition);
        PanelProfileData.ApplyToLive(_store.Load(), exported);
        PanelProfileData.Reset(_store.Load());
        Assert.Null(_store.Load().PanelDevices[id].BackgroundMediaTransition);
        Assert.Null(_store.Load().PanelDevices[id].BackgroundAudioTriggerTransition);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Invalid_imported_transition_is_omitted_without_losing_the_other_transition_or_normal_look(bool mediaInvalid)
    {
        var id = Record();
        _registry.Patch(id, Patch("""
            {"backgroundColor":"#123456","backgroundMediaTransition":{"effect":"gl-transitions:CrossZoom","durationMs":950},
             "backgroundAudioTriggerTransition":{"effect":"curtains:liquid","durationMs":120}}
            """));
        var exported = new NexusSettings();
        PanelProfileData.WriteTo(exported, _store.Load());
        var imported = exported.PanelDevices[id];
        (mediaInvalid ? imported.BackgroundMediaTransition : imported.BackgroundAudioTriggerTransition)!.Effect = "motion:fade";
        var preset = new PanelPreset();
        PanelPresets.Copy(imported, preset);
        Assert.Null(mediaInvalid ? preset.BackgroundMediaTransition : preset.BackgroundAudioTriggerTransition);
        Assert.Equal("#123456", preset.BackgroundColor);
        Assert.NotNull(mediaInvalid ? preset.BackgroundAudioTriggerTransition : preset.BackgroundMediaTransition);
        PanelProfileData.ApplyToLive(_store.Load(), exported);
        var restored = _store.Load().PanelDevices[id];
        Assert.Null(mediaInvalid ? restored.BackgroundMediaTransition : restored.BackgroundAudioTriggerTransition);
        Assert.Equal("#123456", restored.BackgroundColor);
        Assert.NotNull(mediaInvalid ? restored.BackgroundAudioTriggerTransition : restored.BackgroundMediaTransition);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Invalid_transition_rejects_the_entire_registry_patch_before_mutation(bool mediaInvalid)
    {
        var id = Record();
        _registry.Patch(id, new() { BackgroundColor = "#123456", BackgroundMediaTransition = new() { Effect = "curtains:wave" } });
        var invalid = new PanelBackgroundTransitionDto { Effect = "anime:fade" };
        var patch = new PanelDevicePatch
        {
            BackgroundColor = "#abcdef",
            BackgroundMediaTransition = mediaInvalid ? invalid : new() { Effect = "none" },
            BackgroundAudioTriggerTransition = mediaInvalid ? new() { Effect = "none" } : invalid,
        };
        Assert.Throws<ArgumentException>(() => _registry.Patch(id, patch));
        var record = _registry.Get(id)!;
        Assert.Equal("#123456", record.BackgroundColor);
        AssertTransition(record.BackgroundMediaTransition, "curtains:wave", 800);
        Assert.Null(record.BackgroundAudioTriggerTransition);
    }

    private static void AssertTransition(PanelBackgroundTransitionDto? transition, string effect, int durationMs)
    {
        Assert.NotNull(transition);
        Assert.Equal(effect, transition.Effect);
        Assert.Equal(durationMs, transition.DurationMs);
    }
}
