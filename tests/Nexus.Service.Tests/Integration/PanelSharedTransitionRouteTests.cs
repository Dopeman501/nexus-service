using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;

namespace Nexus.Service.Tests.Integration;

public sealed class PanelSharedTransitionRouteTests : IClassFixture<PanelTransitionPreviewHostFactory>
{
    private readonly PanelTransitionPreviewHostFactory _factory;
    public PanelSharedTransitionRouteTests(PanelTransitionPreviewHostFactory factory) => _factory = factory;

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private string Record() => _factory.Services.GetRequiredService<PanelDeviceRegistry>()
        .Allocate("Shared transitions", new PanelDeviceCapabilities { Surface = "lcd-wide" }, canReuse: _ => false).Id;
    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");

    [Fact]
    public async Task One_ordered_setting_roundtrips_without_altering_legacy_preferences_or_background()
    {
        using var client = Client();
        var id = Record();
        using var saved = await client.PostAsync($"/panel/devices/{id}", Json("""
            {"backgroundColor":"#123456","backgroundMediaTransition":{"effect":"curtains:wave","durationMs":900},
             "backgroundTransitions":{"enabled":true,"effects":["gl-transitions:WaterDrop","curtains:wave"],"durationMs":1350}}
            """));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var current = JsonDocument.Parse(await client.GetStringAsync($"/panel/devices/{id}"));
        var record = current.RootElement;
        Assert.Equal("#123456", record.GetProperty("backgroundColor").GetString());
        Assert.Equal(900, record.GetProperty("backgroundMediaTransition").GetProperty("durationMs").GetInt32());
        var transition = record.GetProperty("backgroundTransitions");
        Assert.True(transition.GetProperty("enabled").GetBoolean());
        Assert.Equal(new[] { "gl-transitions:WaterDrop", "curtains:wave" },
            transition.GetProperty("effects").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(1350, transition.GetProperty("durationMs").GetInt32());
    }

    [Fact]
    public async Task Invalid_shared_setting_rejects_other_changes_before_mutation()
    {
        using var client = Client();
        var id = Record();
        using var initial = await client.PostAsync($"/panel/devices/{id}", Json("""{"backgroundColor":"#123456"}"""));
        using var rejected = await client.PostAsync($"/panel/devices/{id}", Json("""
            {"backgroundColor":"#abcdef","backgroundTransitions":{"enabled":true,"effects":["motion:fade"],"durationMs":800}}
            """));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var current = JsonDocument.Parse(await client.GetStringAsync($"/panel/devices/{id}"));
        Assert.Equal("#123456", current.RootElement.GetProperty("backgroundColor").GetString());
    }

    [Fact]
    public async Task Click_preview_roundtrips_separately_from_the_panel_record()
    {
        using var client = Client();
        var id = Record();
        var registry = _factory.Services.GetRequiredService<PanelDeviceRegistry>();
        var before = JsonSerializer.Serialize(registry.Get(id));
        var session = Guid.NewGuid().ToString();
        var preview = Guid.NewGuid().ToString();
        var request = JsonSerializer.Serialize(new { sessionId = session, sequence = 1,
            preview = new { previewId = preview, effect = "curtains:wave", durationMs = 1350 } });
        using var response = await client.PostAsync($"/panel/devices/{id}/transition-preview", Json(request));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var frame = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(id, frame.RootElement.GetProperty("deviceId").GetString());
        Assert.Equal(preview, frame.RootElement.GetProperty("preview").GetProperty("previewId").GetString());
        Assert.True(frame.RootElement.GetProperty("startedAtUnixMs").GetInt64() < frame.RootElement.GetProperty("expiresAtUnixMs").GetInt64());
        using var read = JsonDocument.Parse(await client.GetStringAsync($"/panel/devices/{id}/transition-preview"));
        Assert.Equal(frame.RootElement.GetProperty("revision").GetInt64(), read.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal(before, JsonSerializer.Serialize(registry.Get(id)));
    }

    [Theory]
    [InlineData("17")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("{\"enabled\":null}")]
    [InlineData("{\"enabled\":\"true\"}")]
    [InlineData("{\"enabled\":true,\"effects\":[]}")]
    [InlineData("{\"effects\":null}")]
    [InlineData("{\"effects\":\"curtains:wave\"}")]
    [InlineData("{\"effects\":[null]}")]
    [InlineData("{\"effects\":[\"curtains:wave\",\"curtains:wave\"]}")]
    [InlineData("{\"effects\":[\"none\"]}")]
    [InlineData("{\"effects\":[\"default\"]}")]
    [InlineData("{\"effects\":[\"gl-transitions:waterdrop\"]}")]
    [InlineData("{\"durationMs\":79}")]
    [InlineData("{\"durationMs\":5001}")]
    [InlineData("{\"durationMs\":80.5}")]
    [InlineData("{\"durationMs\":\"800\"}")]
    [InlineData("{\"durationMs\":null}")]
    public async Task Malformed_shared_settings_are_rejected_before_other_preferences_change(string settings)
    {
        using var client = Client();
        var id = Record();
        using var initial = await client.PostAsync($"/panel/devices/{id}", Json("""{"backgroundColor":"#123456"}"""));
        using var rejected = await client.PostAsync($"/panel/devices/{id}", Json("{\"backgroundColor\":\"#ffffff\",\"backgroundTransitions\":" + settings + "}"));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var current = JsonDocument.Parse(await client.GetStringAsync($"/panel/devices/{id}"));
        Assert.Equal("#123456", current.RootElement.GetProperty("backgroundColor").GetString());
    }
}
