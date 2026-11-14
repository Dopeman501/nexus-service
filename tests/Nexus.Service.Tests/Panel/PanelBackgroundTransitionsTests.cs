using System.Text.Json;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Panel;

public sealed class PanelBackgroundTransitionsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "nexus-shared-transition-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly JsonConfigStore _store;
    private readonly PanelDeviceRegistry _registry;
    public PanelBackgroundTransitionsTests() { _store = new(_path); _registry = new(_store); }
    public void Dispose() { _store.Dispose(); File.Delete(_path); }

    private static PanelBackgroundTransitionsDto Settings(bool enabled = true, int durationMs = 1350) =>
        new() { Enabled = enabled, Effects = ["gl-transitions:WaterDrop", "curtains:wave"], DurationMs = durationMs };
    private string Record() => _registry.Allocate("Shared transitions", new() { Surface = "lcd-wide" }).Id;

    [Fact]
    public void Every_custom_catalogue_effect_is_valid_and_the_full_selection_preserves_order()
    {
        var effects = PanelBackgroundTransitionCatalogue.Effects.Where(PanelBackgroundTransitionsPolicy.IsCustomEffect)
            .OrderBy(effect => effect, StringComparer.Ordinal).ToList();
        Assert.Equal(226, effects.Count);
        Assert.True(PanelBackgroundTransitionsPolicy.IsValid(new() { Enabled = true, Effects = effects }));
        var copy = PanelBackgroundTransitionsPolicy.Clone(new() { Enabled = true, Effects = effects })!;
        Assert.Equal(effects, copy.Effects);
        Assert.NotSame(effects, copy.Effects);
        foreach (var effect in effects) Assert.True(PanelBackgroundTransitionsPolicy.IsValid(new() { Enabled = true, Effects = [effect] }));
    }

    [Theory]
    [InlineData(80)]
    [InlineData(800)]
    [InlineData(5000)]
    public void Boundaries_roundtrip_in_both_source_generated_contexts(int duration)
    {
        var json = JsonSerializer.Serialize(Settings(durationMs: duration), AppJsonContext.Default.PanelBackgroundTransitionsDto);
        var restored = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.PanelBackgroundTransitionsDto)!;
        Assert.True(restored.Enabled);
        Assert.Equal(duration, restored.DurationMs);
        Assert.Equal(Settings().Effects, restored.Effects);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(79)]
    [InlineData(5001)]
    public void Invalid_duration_is_rejected_and_omitted_from_imports(int duration)
    {
        Assert.False(PanelBackgroundTransitionsPolicy.IsValid(Settings(durationMs: duration)));
        Assert.Null(PanelBackgroundTransitionsPolicy.Clone(Settings(durationMs: duration)));
    }

    [Theory]
    [InlineData("default")]
    [InlineData("none")]
    [InlineData("motion:fade")]
    [InlineData("anime:fade")]
    [InlineData("codrops:fade")]
    [InlineData("curtains:unknown")]
    [InlineData("gl-transitions:waterdrop")]
    [InlineData("")]
    [InlineData(null)]
    public void Invalid_or_legacy_effects_cannot_enter_the_shared_cycle(string? effect)
    {
        Assert.False(PanelBackgroundTransitionsPolicy.IsValid(new() { Effects = [effect!] }));
        Assert.False(PanelBackgroundTransitionsPolicy.IsCustomEffect(effect));
    }

    [Fact]
    public void Empty_disabled_selection_is_valid_but_duplicates_null_and_unbounded_collections_are_not()
    {
        Assert.True(PanelBackgroundTransitionsPolicy.IsValid(null));
        Assert.Null(PanelBackgroundTransitionsPolicy.Clone(null));
        Assert.True(PanelBackgroundTransitionsPolicy.IsValid(new()));
        Assert.False(PanelBackgroundTransitionsPolicy.IsValid(new() { Enabled = true }));
        Assert.False(PanelBackgroundTransitionsPolicy.IsValid(new() { Effects = null! }));
        Assert.False(PanelBackgroundTransitionsPolicy.IsValid(new() { Effects = ["curtains:wave", "curtains:wave"] }));
        Assert.False(PanelBackgroundTransitionsPolicy.IsValid(new() { Effects = Enumerable.Repeat("curtains:wave", 227).ToList() }));
        Assert.False(PanelBackgroundTransitionsPolicy.IsCustomEffect(new string('x', 129)));
    }

    [Fact]
    public void Patch_readback_preset_and_profile_clones_are_independent_and_reset_clears_shared_state()
    {
        var id = Record();
        Assert.Null(_registry.Get(id)!.BackgroundTransitions);
        var input = Settings();
        var saved = _registry.Patch(id, new() { BackgroundColor = "#123456", BackgroundTransitions = input })!;
        input.Effects[0] = "curtains:liquid";
        saved.BackgroundTransitions!.Effects.Clear();
        var record = _registry.Get(id)!;
        Assert.Equal(Settings().Effects, record.BackgroundTransitions!.Effects);
        Assert.True(record.BackgroundTransitions.Enabled);
        var preset = new PanelPreset();
        PanelPresets.Copy(record, preset);
        preset.BackgroundTransitions!.Effects.Reverse();
        Assert.Equal(Settings().Effects, record.BackgroundTransitions.Effects);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(_store.Load(), PersistenceJsonContext.Default.NexusSettings);
        var restored = JsonSerializer.Deserialize(bytes, PersistenceJsonContext.Default.NexusSettings)!.PanelDevices[id];
        Assert.Equal(Settings().Effects, restored.BackgroundTransitions!.Effects);
        var exported = new NexusSettings();
        PanelProfileData.WriteTo(exported, _store.Load());
        Assert.False(PanelProfileData.IsDefaultLook(record));
        _registry.ResetToDefaults(id);
        Assert.Null(_registry.Get(id)!.BackgroundTransitions);
        PanelProfileData.ApplyToLive(_store.Load(), exported);
        Assert.Equal(Settings().Effects, _store.Load().PanelDevices[id].BackgroundTransitions!.Effects);
        PanelProfileData.Reset(_store.Load());
        Assert.Null(_store.Load().PanelDevices[id].BackgroundTransitions);
    }

    [Fact]
    public void Invalid_shared_import_is_omitted_without_losing_the_normal_look_or_legacy_transition()
    {
        var source = new PanelDeviceRecord { BackgroundColor = "#abcdef", BackgroundTransitions = Settings(),
            BackgroundMediaTransition = new() { Effect = "curtains:wave" } };
        source.BackgroundTransitions.Effects.Add("motion:fade");
        var preset = new PanelPreset();
        PanelPresets.Copy(source, preset);
        Assert.Null(preset.BackgroundTransitions);
        Assert.Equal("#abcdef", preset.BackgroundColor);
        Assert.Equal("curtains:wave", preset.BackgroundMediaTransition!.Effect);
    }

    [Fact]
    public void Disabled_selection_retains_its_effect_order_and_invalid_registry_patch_is_atomic()
    {
        var id = Record();
        var initial = _registry.Patch(id, new() { BackgroundTransitions = Settings(enabled: false), BackgroundColor = "#123456" })!;
        Assert.False(initial.BackgroundTransitions!.Enabled);
        Assert.Equal(Settings().Effects, initial.BackgroundTransitions.Effects);
        Assert.Throws<ArgumentException>(() => _registry.Patch(id, new() {
            BackgroundColor = "#ffffff", BackgroundTransitions = new() { Enabled = true, Effects = ["motion:fade"] } }));
        var current = _registry.Get(id)!;
        Assert.Equal("#123456", current.BackgroundColor);
        Assert.Equal(Settings().Effects, current.BackgroundTransitions!.Effects);
    }
}
