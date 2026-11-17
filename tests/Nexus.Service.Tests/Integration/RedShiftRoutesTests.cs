using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Integration;

public sealed class RedShiftRoutesTests : IClassFixture<StubDeviceHostFactory>
{
    private readonly HttpClient _client;
    private readonly IConfigStore _store;

    public RedShiftRoutesTests(StubDeviceHostFactory factory)
    {
        factory.ResetSettings();
        _store = factory.Services.GetRequiredService<IConfigStore>();
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", factory.Services.GetRequiredService<TokenService>().Token);
    }

    [Fact]
    public async Task Red_shift_only_write_is_visible_and_independent_per_device()
    {
        Assert.Equal(HttpStatusCode.OK, await Post("""{"ids":["dev-a"],"redShift":true}"""));
        using var doc = await Read();
        var adjustments = doc.RootElement.GetProperty("adjustments");
        Assert.True(adjustments.GetProperty("dev-a").GetProperty("redShift").GetBoolean());
        Assert.False(adjustments.TryGetProperty("dev-b", out _));
        Assert.False(_store.Load().Devices.LightingDevicePrefs.ContainsKey("dev-b"));
    }

    [Fact]
    public async Task Disabling_returns_a_neutral_device_to_the_sparse_default()
    {
        await Post("""{"ids":["dev-a"],"redShift":true}""");
        Assert.Equal(HttpStatusCode.OK, await Post("""{"ids":["dev-a"],"redShift":false}"""));
        using var doc = await Read();
        Assert.False(doc.RootElement.GetProperty("adjustments").TryGetProperty("dev-a", out _));
    }

    [Fact]
    public async Task Colour_tuning_reset_preserves_red_shift_and_red_shift_preserves_tuning()
    {
        await Post("""{"ids":["dev-a"],"red":1.4,"colorOrder":"GRB","brightness":55}""");
        await Post("""{"ids":["dev-a"],"redShift":true}""");
        var pref = _store.Load().Devices.LightingDevicePrefs["dev-a"];
        Assert.Equal(1.4f, pref.AdjustRed);
        Assert.Equal("GRB", pref.ColorOrder);
        Assert.Equal(55, pref.Brightness);
        await Post("""{"ids":["dev-a"],"red":1,"green":1,"blue":1,"temperature":0,"saturation":1,"colorOrder":"RGB"}""");
        using var doc = await Read();
        Assert.True(doc.RootElement.GetProperty("adjustments").GetProperty("dev-a").GetProperty("redShift").GetBoolean());
    }

    [Fact]
    public async Task Preset_capture_and_activation_preserve_red_shift_without_aliasing()
    {
        await Post("""{"ids":["dev-a"],"redShift":true}""");
        var create = await _client.PostAsync("/devices/lighting-devices/layout-presets", Json("""{"name":"Red"}"""));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = created.RootElement.GetProperty("preset").GetProperty("id").GetString();
        var captured = _store.Load().Lighting.LayoutPresets[0].DevicePrefs!["dev-a"];
        using var saved = JsonDocument.Parse(JsonSerializer.Serialize(captured, PersistenceJsonContext.Default.LightingDevicePreference));
        Assert.True(saved.RootElement.GetProperty("redShift").GetBoolean());
        _store.Update(s => s.Devices.LightingDevicePrefs["dev-a"] = new LightingDevicePreference());
        var activate = await _client.PostAsync($"/devices/lighting-devices/layout-presets/{id}/activate", Json("{}"));
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        using var doc = await Read();
        Assert.True(doc.RootElement.GetProperty("adjustments").GetProperty("dev-a").GetProperty("redShift").GetBoolean());
        Assert.NotSame(captured, _store.Load().Devices.LightingDevicePrefs["dev-a"]);
    }

    [Fact]
    public async Task Invalid_boolean_is_rejected_without_a_preference_write()
    {
        Assert.Equal(HttpStatusCode.BadRequest, await Post("""{"ids":["dev-a"],"redShift":"true"}"""));
        Assert.False(_store.Load().Devices.LightingDevicePrefs.ContainsKey("dev-a"));
    }

    private async Task<HttpStatusCode> Post(string body) =>
        (await _client.PostAsync("/devices/lighting-devices/color-adjust", Json(body))).StatusCode;

    private async Task<JsonDocument> Read() => JsonDocument.Parse(
        await _client.GetStringAsync("/devices/lighting-devices/color-adjust"));

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}
