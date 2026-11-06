using System.Text.Json;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Panel;

public sealed class PanelAudioTriggerTests : IDisposable
{
    private const string Settings = "{\"enabled\":true,\"intensity\":65,\"switchDelay\":0.5,\"shuffle\":true,\"backgrounds\":[{\"mode\":\"solid\",\"color\":\"#123456\"},{\"mode\":\"shader\",\"effect\":\"plasma\",\"effectState\":{\"speed\":10}},{\"mode\":\"media\",\"mediaId\":\"asset-1\",\"mediaType\":\"animated\"}]}";
    private readonly string _path = Path.Combine(Path.GetTempPath(), "nexus-audio-trigger-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly JsonConfigStore _store;
    private readonly PanelDeviceRegistry _registry;

    public PanelAudioTriggerTests()
    {
        _store = new JsonConfigStore(_path);
        _registry = new PanelDeviceRegistry(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        File.Delete(_path);
    }

    private static PanelDevicePatch Patch(string settings = Settings) => JsonSerializer.Deserialize(
        "{\"backgroundAudioTrigger\":" + settings + "}", AppJsonContext.Default.PanelDevicePatch)!;

    private static JsonElement Read(PanelDeviceRecord record) => JsonSerializer.SerializeToElement(record, AppJsonContext.Default.PanelDeviceRecord);

    [Fact]
    public void Audio_backgrounds_persist_alongside_the_standard_selection_and_survive_partial_patches()
    {
        var id = _registry.Allocate("Audio", new() { Surface = "lcd-wide" }).Id;
        _registry.Patch(id, new() { BackgroundMode = "media", BackgroundMediaId = "normal", BackgroundMediaType = "static" });
        var changed = _registry.Patch(id, Patch())!;
        Assert.Equal("normal", changed.BackgroundMediaId);
        Assert.Equal(3, Read(changed).GetProperty("backgroundAudioTrigger").GetProperty("backgrounds").GetArrayLength());
        var partial = _registry.Patch(id, new() { AccentColor = "#ffffff" })!;
        Assert.Equal(0.5, Read(partial).GetProperty("backgroundAudioTrigger").GetProperty("switchDelay").GetDouble());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(_store.Load(), PersistenceJsonContext.Default.NexusSettings);
        var restored = JsonSerializer.Deserialize(bytes, PersistenceJsonContext.Default.NexusSettings)!.PanelDevices[id];
        Assert.True(JsonElement.DeepEquals(Read(changed).GetProperty("backgroundAudioTrigger"), Read(restored).GetProperty("backgroundAudioTrigger")));
    }

    [Fact]
    public void Presets_and_profile_copies_carry_the_audio_selection_and_reset_clears_it()
    {
        var id = _registry.Allocate("Audio", new() { Surface = "lcd-wide" }).Id;
        var record = _registry.Patch(id, Patch())!;
        var preset = new PanelPreset();
        PanelPresets.Copy(record, preset);
        var copy = new PanelDeviceRecord();
        PanelPresets.Copy(preset, copy);
        Assert.True(Read(copy).GetProperty("backgroundAudioTrigger").GetProperty("enabled").GetBoolean());
        Assert.False(PanelProfileData.IsDefaultLook(copy));
        var reset = _registry.ResetToDefaults(id)!;
        Assert.False(Read(reset).TryGetProperty("backgroundAudioTrigger", out _));
    }

    [Fact]
    public void Registry_and_preset_snapshots_are_deep_copies_including_shader_state()
    {
        var id = _registry.Allocate("Audio", new() { Surface = "lcd-wide" }).Id;
        var patch = Patch();
        var saved = _registry.Patch(id, patch)!;
        patch.BackgroundAudioTrigger!.Backgrounds![0].Color = "#ffffff";
        saved.BackgroundAudioTrigger!.Backgrounds![1].EffectState = JsonDocument.Parse("{\"speed\":99}").RootElement.Clone();
        saved.BackgroundAudioTrigger.Backgrounds.Clear();
        var record = _registry.Get(id)!;
        Assert.Equal("#123456", record.BackgroundAudioTrigger!.Backgrounds![0].Color);
        Assert.Equal(10, record.BackgroundAudioTrigger.Backgrounds[1].EffectState!.Value.GetProperty("speed").GetInt32());
        var preset = new PanelPreset();
        PanelPresets.Copy(record, preset);
        preset.BackgroundAudioTrigger!.Backgrounds![0].Color = "#eeeeee";
        preset.BackgroundAudioTrigger.Backgrounds.Clear();
        Assert.Equal(3, record.BackgroundAudioTrigger.Backgrounds.Count);
        Assert.Equal("#123456", record.BackgroundAudioTrigger.Backgrounds[0].Color);
    }

    [Fact]
    public void Profiles_keep_audio_selection_and_drop_malformed_imports_without_changing_normal_background()
    {
        var id = _registry.Allocate("Audio", new() { Surface = "lcd-wide" }).Id;
        _registry.Patch(id, Patch());
        var exported = new NexusSettings();
        PanelProfileData.WriteTo(exported, _store.Load());
        Assert.True(exported.PanelDevices[id].BackgroundAudioTrigger!.Enabled);
        _registry.ResetToDefaults(id);
        PanelProfileData.ApplyToLive(_store.Load(), exported);
        Assert.True(_store.Load().PanelDevices[id].BackgroundAudioTrigger!.Enabled);
        exported.PanelDevices[id].BackgroundAudioTrigger!.Intensity = double.NaN;
        exported.PanelDevices[id].BackgroundMode = "solid";
        exported.PanelDevices[id].BackgroundColor = "#abcdef";
        PanelProfileData.ApplyToLive(_store.Load(), exported);
        Assert.Null(_store.Load().PanelDevices[id].BackgroundAudioTrigger);
        Assert.Equal("#abcdef", _store.Load().PanelDevices[id].BackgroundColor);
    }

    [Fact]
    public void Missing_configuration_stays_absent_and_an_empty_selection_has_safe_defaults()
    {
        var id = _registry.Allocate("Audio", new() { Surface = "lcd-wide" }).Id;
        Assert.False(Read(_registry.Get(id)!).TryGetProperty("backgroundAudioTrigger", out _));
        var defaults = _registry.Patch(id, Patch("{}"))!.BackgroundAudioTrigger!;
        Assert.False(defaults.Enabled);
        Assert.Equal(50, defaults.Intensity);
        Assert.Equal(1, defaults.SwitchDelay);
        Assert.False(defaults.Shuffle);
        Assert.Empty(defaults.Backgrounds!);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 10)]
    [InlineData(50, 0.5)]
    public void Valid_scalar_endpoints_and_fractional_delays_are_accepted(double intensity, double delay)
    {
        Assert.True(PanelAudioTriggerPolicy.IsValid(new() { Intensity = intensity, SwitchDelay = delay }));
    }

    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    [InlineData(50, double.NaN)]
    [InlineData(50, double.NegativeInfinity)]
    public void Nonfinite_settings_are_rejected_and_never_cloned(double intensity, double delay)
    {
        var settings = new PanelAudioTriggerDto { Intensity = intensity, SwitchDelay = delay };
        Assert.False(PanelAudioTriggerPolicy.IsValid(settings));
        Assert.Null(PanelAudioTriggerPolicy.Clone(settings));
    }

    [Fact]
    public void Selection_count_and_total_state_are_bounded()
    {
        var settings = new PanelAudioTriggerDto { Backgrounds = Enumerable.Range(0, 32)
            .Select(_ => new D215PanelBackgroundDto { Mode = "solid", Color = "#123456" }).ToList() };
        Assert.True(PanelAudioTriggerPolicy.IsValid(settings));
        settings.Backgrounds.Add(new() { Mode = "solid" });
        Assert.False(PanelAudioTriggerPolicy.IsValid(settings));
        // Each shader stays below its own 32-KiB limit while the selection exceeds 128 KiB.
        var sensor = "{\"category\":\"quick\",\"sensor\":\"" + new string('a', 500) + "\",\"min\":0,\"max\":100}";
        var state = JsonDocument.Parse("{\"sensors\":{" + string.Join(',', Enumerable.Range(0, 40).Select(i => $"\"u_{i}\":" + sensor)) + "}}").RootElement.Clone();
        settings.Backgrounds = Enumerable.Range(0, 8).Select(_ => new D215PanelBackgroundDto { Mode = "shader", Effect = "plasma", EffectState = state }).ToList();
        Assert.False(PanelAudioTriggerPolicy.IsValid(settings));
        Assert.Null(PanelAudioTriggerPolicy.Clone(settings));
    }

    [Theory]
    [InlineData("{\"intensity\":-1}")]
    [InlineData("{\"intensity\":101}")]
    [InlineData("{\"switchDelay\":-0.1}")]
    [InlineData("{\"switchDelay\":10.1}")]
    [InlineData("{\"backgrounds\":[null]}")]
    [InlineData("{\"backgrounds\":[{\"mode\":\"desktop\"}]}")]
    [InlineData("{\"backgrounds\":[{\"mode\":\"media\",\"mediaId\":\"../file\"}]}")]
    [InlineData("{\"backgrounds\":[{\"mode\":\"solid\",\"mediaOrder\":[\"item\"]}]}")]
    public void Invalid_audio_settings_do_not_replace_the_current_selection(string invalid)
    {
        var id = _registry.Allocate("Audio", new() { Surface = "lcd-wide" }).Id;
        _registry.Patch(id, Patch());
        Assert.Throws<ArgumentException>(() => _registry.Patch(id, Patch(invalid)));
        Assert.Equal(65, Read(_registry.Get(id)!).GetProperty("backgroundAudioTrigger").GetProperty("intensity").GetDouble());
    }
}
