using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;

namespace Nexus.Service.Tests.Integration;

public sealed class PanelBackgroundTransitionRouteTests : IDisposable
{
    private readonly NexusAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private string Record() => _factory.Services.GetRequiredService<PanelDeviceRegistry>()
        .Allocate("Transitions", new PanelDeviceCapabilities { Surface = "lcd-wide" }, canReuse: _ => false).Id;

    [Fact]
    public async Task Independent_slideshow_and_audio_transitions_roundtrip_with_normal_background_unchanged()
    {
        using var client = Client();
        var id = Record();
        const string body = """
            {"backgroundMode":"solid","backgroundColor":"#123456",
             "backgroundMediaTransition":{"effect":"gl-transitions:CrossZoom","durationMs":950},
             "backgroundAudioTriggerTransition":{"effect":"curtains:liquid","durationMs":120}}
            """;
        using var saved = await client.PostAsync($"/panel/devices/{id}", Json(body));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var response = await client.GetAsync($"/panel/devices/{id}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var record = json.RootElement;
        Assert.Equal("#123456", record.GetProperty("backgroundColor").GetString());
        Assert.Equal("gl-transitions:CrossZoom", record.GetProperty("backgroundMediaTransition").GetProperty("effect").GetString());
        Assert.Equal(120, record.GetProperty("backgroundAudioTriggerTransition").GetProperty("durationMs").GetInt32());
    }

    [Fact]
    public async Task Invalid_transition_is_rejected_before_other_fields_are_mutated()
    {
        using var client = Client();
        var id = Record();
        using var initial = await client.PostAsync($"/panel/devices/{id}", Json("""{"backgroundColor":"#123456"}"""));
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        using var rejected = await client.PostAsync($"/panel/devices/{id}", Json("""
            {"backgroundColor":"#abcdef","backgroundMediaTransition":{"effect":"motion:fade","durationMs":800}}
            """));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var response = await client.GetAsync($"/panel/devices/{id}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("#123456", json.RootElement.GetProperty("backgroundColor").GetString());
    }

    public static IEnumerable<object[]> MalformedSettings()
    {
        string[] settings =
        [
            "17", "[]", "true", "\"curtains:wave\"", "{\"effect\":null}", "{\"effect\":17}", "{\"effect\":\"\"}",
            "{\"effect\":\"motion:fade\"}", "{\"effect\":\"anime:fade\"}", "{\"effect\":\"codrops:displacement\"}",
            "{\"effect\":\"curtains:unknown\"}", "{\"effect\":\"gl-transitions:crosszoom\"}",
            "{\"durationMs\":79}", "{\"durationMs\":5001}", "{\"durationMs\":80.5}",
            "{\"durationMs\":\"800\"}", "{\"durationMs\":null}", "{\"durationMs\":true}",
        ];
        foreach (var field in new[] { "backgroundMediaTransition", "backgroundAudioTriggerTransition" })
            foreach (var setting in settings) yield return [field, setting];
    }

    [Theory]
    [MemberData(nameof(MalformedSettings))]
    public async Task Malformed_or_excluded_transition_is_rejected_in_either_field(string field, string settings)
    {
        using var client = Client();
        var id = Record();
        using var response = await client.PostAsync($"/panel/devices/{id}", Json($$"""{"{{field}}":{{settings}}}"""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");
}
