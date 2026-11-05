using System.Text.Json;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Widgets;

namespace Nexus.Service.Tests.Panel;

public sealed class D215PanelBackgroundTests
{
    private static readonly AppRegistry Apps = new(() => []);
    private static readonly PanelDeviceCapabilities Target = new() { Family = "jungle-leopard-d215", Surface = "lcd-wide" };

    private static PanelLayoutDto Layout(string background, bool enabled = false) => JsonSerializer.Deserialize(
        "{\"surface\":\"lcd-wide\",\"pages\":[],\"d215Panels\":{\"enabled\":false,\"backgroundsEnabled\":"
        + enabled.ToString().ToLowerInvariant() + ",\"items\":[{\"id\":\"circle\",\"type\":\"clock\",\"x\":0.5,\"y\":0.5,\"diameter\":0.4,\"background\":" + background + "}]}}",
        AppJsonContext.Default.PanelLayoutDto)!;

    private static JsonElement Read(PanelLayoutDto layout) => JsonSerializer.SerializeToElement(layout, AppJsonContext.Default.PanelLayoutDto);

    [Theory]
    [InlineData("{\"mode\":\"solid\",\"color\":\"#AAbb00\",\"colorLight\":\"#ffffff\",\"opacity\":0,\"frost\":100}")]
    [InlineData("{\"mode\":\"media\",\"mediaId\":\"asset-1_A\",\"mediaType\":\"animated\",\"mediaAlpha\":true,\"slideshow\":true,\"interval\":180,\"shuffle\":true,\"finishVideos\":false,\"mediaOrder\":[\"asset-1_A\",\"other\"]}")]
    [InlineData("{\"mode\":\"shader\",\"effect\":\"plasma\",\"template\":3,\"effectState\":{\"speed\":-100,\"intensity\":1,\"hue\":0,\"colorize\":0,\"saturation\":2,\"contrast\":1,\"params\":{\"u_zoom\":2},\"sensors\":{\"u_temp\":{\"category\":\"quick\",\"sensor\":\"summary/cpu-usage\",\"min\":0,\"max\":100}}}}")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"effectState\":{\"saturation\":4,\"contrast\":4}}")]
    public void Valid_backgrounds_are_accepted_and_preserved_even_while_disabled(string background)
    {
        var layout = Layout(background);
        Assert.True(D215PanelsPolicy.IsValid(layout, Target, Apps, null));
        var saved = Read(layout).GetProperty("d215Panels");
        Assert.False(saved.GetProperty("backgroundsEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Object, saved.GetProperty("items")[0].ValueKind);
        if (background != "null")
        {
            using var expected = JsonDocument.Parse(background);
            Assert.True(JsonElement.DeepEquals(expected.RootElement, saved.GetProperty("items")[0].GetProperty("background")));
        }
    }

    [Theory]
    [InlineData("{\"mode\":\"desktop\"}")]
    [InlineData("{\"color\":\"red\"}")]
    [InlineData("{\"colorLight\":\"#123\"}")]
    [InlineData("{\"color\":\"#zzzzzz\"}")]
    [InlineData("{\"effect\":\"../plasma\"}")]
    [InlineData("{\"template\":-1}")]
    [InlineData("{\"template\":4}")]
    [InlineData("{\"opacity\":1.01}")]
    [InlineData("{\"opacity\":-0.01}")]
    [InlineData("{\"frost\":101}")]
    [InlineData("{\"frost\":-1}")]
    [InlineData("{\"interval\":120}")]
    [InlineData("{\"mediaId\":\"../file\"}")]
    [InlineData("{\"mediaType\":\"video\"}")]
    [InlineData("{\"mediaOrder\":[\"valid\",\"../invalid\"]}")]
    [InlineData("{\"mediaOrder\":[null]}")]
    [InlineData("{\"effectState\":[]}")]
    [InlineData("{\"effectState\":{\"speed\":101}}")]
    [InlineData("{\"effectState\":{\"hue\":2}}")]
    [InlineData("{\"effectState\":{\"saturation\":4.01}}")]
    [InlineData("{\"effectState\":{\"contrast\":4.01}}")]
    [InlineData("{\"effectState\":{\"params\":{\"u_zoom\":\"2\"}}}")]
    [InlineData("{\"effectState\":{\"params\":[]}}")]
    [InlineData("{\"effectState\":{\"params\":{\"../zoom\":2}}}")]
    [InlineData("{\"effectState\":{\"params\":{\"u_zoom\":1e999}}}")]
    [InlineData("{\"effectState\":{\"unknown\":1}}")]
    [InlineData("{\"effectState\":{\"speed\":1,\"speed\":2}}")]
    [InlineData("{\"effectState\":{\"sensors\":{\"u_temp\":{\"category\":\"quick\",\"sensor\":\"cpu\",\"min\":100,\"max\":0}}}}")]
    public void Invalid_backgrounds_are_rejected_even_while_disabled(string background)
    {
        var layout = Layout(background);
        Assert.False(D215PanelsPolicy.IsValid(layout, Target, Apps, null));
        Assert.Null(D215PanelsPolicy.ForTarget(layout, Target)!.D215Panels);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(180)]
    [InlineData(300)]
    [InlineData(900)]
    [InlineData(1800)]
    [InlineData(3600)]
    public void Existing_slideshow_intervals_remain_available_to_each_circle(int interval)
    {
        Assert.True(D215PanelsPolicy.IsValid(Layout("{\"interval\":" + interval + "}"), Target, Apps, null));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"category\":1,\"sensor\":\"cpu\",\"min\":0,\"max\":100}")]
    [InlineData("{\"category\":\"bad_category\",\"sensor\":\"cpu\",\"min\":0,\"max\":100}")]
    [InlineData("{\"category\":\"quick\",\"sensor\":null,\"min\":0,\"max\":100}")]
    [InlineData("{\"category\":\"quick\",\"sensor\":\"\",\"min\":0,\"max\":100}")]
    [InlineData("{\"category\":\"quick\",\"sensor\":\"cpu\",\"max\":100}")]
    [InlineData("{\"category\":\"quick\",\"sensor\":\"cpu\",\"min\":\"0\",\"max\":100}")]
    [InlineData("{\"category\":\"quick\",\"sensor\":\"cpu\",\"min\":0}")]
    [InlineData("{\"category\":\"quick\",\"sensor\":\"cpu\",\"min\":0,\"max\":1000000001}")]
    [InlineData("{\"category\":\"quick\",\"sensor\":\"cpu\",\"min\":0,\"max\":100,\"extra\":0}")]
    public void Invalid_shader_sensor_overrides_are_rejected(string sensor)
    {
        var background = "{\"effectState\":{\"sensors\":{\"u_temp\":" + sensor + "}}}";
        Assert.False(D215PanelsPolicy.IsValid(Layout(background), Target, Apps, null));
    }

    [Fact]
    public void Background_resource_bounds_reject_excessive_identifiers_collections_and_state()
    {
        var tooManyParams = string.Join(',', Enumerable.Range(0, 129).Select(i => $"\"u_{i}\":0"));
        var tooManyMedia = string.Join(',', Enumerable.Repeat("\"asset\"", 1001));
        var sensor = "{\"category\":\"quick\",\"sensor\":\"" + new string('x', 512) + "\",\"min\":0,\"max\":100}";
        var oversizedState = "{\"sensors\":{" + string.Join(',', Enumerable.Range(0, 64).Select(i => $"\"u_{i}\":" + sensor)) + "}}";
        var invalid = new[]
        {
            "{\"effect\":\"" + new string('a', 129) + "\"}",
            "{\"mediaId\":\"" + new string('a', 65) + "\"}",
            "{\"mediaOrder\":[" + tooManyMedia + "]}",
            "{\"effectState\":{\"params\":{" + tooManyParams + "}}}",
            "{\"effectState\":" + oversizedState + "}",
        };
        Assert.All(invalid, background => Assert.False(D215PanelsPolicy.IsValid(Layout(background), Target, Apps, null)));
    }

    [Fact]
    public void Backgrounds_roundtrip_independently_and_presets_deep_clone_them()
    {
        var first = Layout("{\"mode\":\"media\",\"mediaId\":\"first\",\"mediaOrder\":[\"first\",\"second\"]}", enabled: true);
        first.D215Panels!.Items.Add(Layout("{\"mode\":\"shader\",\"effect\":\"plasma\",\"effectState\":{\"speed\":10,\"params\":{\"u_zoom\":2}}}").D215Panels!.Items[0]);
        first.D215Panels.Items[1].Id = "second";
        var clone = PanelPresets.CloneLayout(first)!;
        var settings = new NexusSettings();
        settings.PanelDevices["d215"] = new() { Id = "d215", Layout = clone };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, PersistenceJsonContext.Default.NexusSettings);
        var restored = JsonSerializer.Deserialize(bytes, PersistenceJsonContext.Default.NexusSettings)!.PanelDevices["d215"].Layout!;
        var panels = Read(restored).GetProperty("d215Panels");
        Assert.True(panels.GetProperty("backgroundsEnabled").GetBoolean());
        Assert.Equal("first", panels.GetProperty("items")[0].GetProperty("background").GetProperty("mediaId").GetString());
        Assert.Equal(10, panels.GetProperty("items")[1].GetProperty("background").GetProperty("effectState").GetProperty("speed").GetInt32());
        clone.D215Panels!.Items[0].Background!.MediaOrder!.Add("third");
        clone.D215Panels.Items[1].Background!.EffectState = JsonDocument.Parse("{\"speed\":50}").RootElement.Clone();
        clone.D215Panels.BackgroundsEnabled = false;
        Assert.Equal(2, first.D215Panels.Items[0].Background!.MediaOrder!.Count);
        Assert.Equal(10, first.D215Panels.Items[1].Background!.EffectState!.Value.GetProperty("speed").GetInt32());
        Assert.True(first.D215Panels.BackgroundsEnabled);
    }

    [Fact]
    public void Absent_background_switch_preserves_the_legacy_default_without_materializing_settings()
    {
        var layout = JsonSerializer.Deserialize("{\"d215Panels\":{\"items\":[]}}", AppJsonContext.Default.PanelLayoutDto)!;
        Assert.Null(layout.D215Panels!.BackgroundsEnabled);
        Assert.True(D215PanelsPolicy.IsValid(layout, Target, Apps, null));
        Assert.False(Read(layout).GetProperty("d215Panels").TryGetProperty("backgroundsEnabled", out _));
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(1, double.NaN)]
    [InlineData(1, double.NegativeInfinity)]
    public void Nonfinite_background_scalars_never_reach_a_renderer(double opacity, double frost)
    {
        var layout = Layout("{}");
        layout.D215Panels!.Items[0].Background!.Opacity = opacity;
        layout.D215Panels.Items[0].Background!.Frost = frost;
        Assert.False(D215PanelsPolicy.IsValid(layout, Target, Apps, null));
    }
}
