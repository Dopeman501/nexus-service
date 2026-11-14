using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

public sealed class PanelAudioTriggerBeatTransitionRouteTests : IClassFixture<PanelTransitionPreviewHostFactory>
{
    private readonly PanelTransitionPreviewHostFactory _factory;
    public PanelAudioTriggerBeatTransitionRouteTests(PanelTransitionPreviewHostFactory factory) => _factory = factory;

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private string Record()
    {
        var id = _factory.Services.GetRequiredService<PanelDeviceRegistry>()
            .Allocate("Beat transitions", new PanelDeviceCapabilities { Surface = "lcd-wide" }, canReuse: _ => false).Id;
        _factory.Services.GetRequiredService<IConfigStore>().Update(settings =>
        {
            settings.PanelDevices[id].BackgroundTransitions = new()
            {
                Enabled = true, Effects = ["curtains:wave", "gl-transitions:WaterDrop"], DurationMs = 1700,
            };
            settings.PanelDevices[id].BackgroundColor = "#123456";
        });
        return id;
    }

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Beat_preference_roundtrips_without_editing_main_selection_or_audio_detector()
    {
        using var client = Client();
        var id = Record();
        using var saved = await client.PostAsync($"/panel/devices/{id}", Json("""
            {"backgroundAudioTriggerBeatTransition":{"enabled":false,"durationMs":240}}
            """));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var current = JsonDocument.Parse(await client.GetStringAsync($"/panel/devices/{id}"));
        var root = current.RootElement;
        var beat = root.GetProperty("backgroundAudioTriggerBeatTransition");
        Assert.False(beat.GetProperty("enabled").GetBoolean());
        Assert.Equal(240, beat.GetProperty("durationMs").GetInt32());
        Assert.Equal(1700, root.GetProperty("backgroundTransitions").GetProperty("durationMs").GetInt32());
        Assert.Equal("#123456", root.GetProperty("backgroundColor").GetString());
        Assert.False(root.TryGetProperty("backgroundAudioTrigger", out _));
    }

    [Fact]
    public async Task First_main_edit_freezes_old_inherited_beat_preference_then_future_main_changes_stay_independent()
    {
        using var client = Client();
        var id = Record();
        using (var before = JsonDocument.Parse(await client.GetStringAsync($"/panel/devices/{id}")))
            Assert.False(before.RootElement.TryGetProperty("backgroundAudioTriggerBeatTransition", out _));
        using var first = await client.PostAsync($"/panel/devices/{id}", Json("""
            {"backgroundTransitions":{"enabled":false,"effects":["curtains:wave"],"durationMs":5000}}
            """));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var after = JsonDocument.Parse(await client.GetStringAsync($"/panel/devices/{id}"));
        var beat = after.RootElement.GetProperty("backgroundAudioTriggerBeatTransition");
        Assert.True(beat.GetProperty("enabled").GetBoolean());
        Assert.Equal(1700, beat.GetProperty("durationMs").GetInt32());
        using var later = await client.PostAsync($"/panel/devices/{id}", Json("""
            {"backgroundTransitions":{"enabled":true,"effects":["gl-transitions:WaterDrop"],"durationMs":80}}
            """));
        using var final = JsonDocument.Parse(await client.GetStringAsync($"/panel/devices/{id}"));
        Assert.Equal(1700, final.RootElement.GetProperty("backgroundAudioTriggerBeatTransition").GetProperty("durationMs").GetInt32());
    }

    [Theory]
    [InlineData("17")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("{\"enabled\":null}")]
    [InlineData("{\"enabled\":\"true\"}")]
    [InlineData("{\"durationMs\":79}")]
    [InlineData("{\"durationMs\":5001}")]
    [InlineData("{\"durationMs\":80.5}")]
    [InlineData("{\"durationMs\":\"800\"}")]
    [InlineData("{\"durationMs\":null}")]
    public async Task Invalid_beat_patch_rejects_all_changes_before_main_or_other_fields_mutate(string beat)
    {
        using var client = Client();
        var id = Record();
        using var response = await client.PostAsync($"/panel/devices/{id}", Json("""
            {"backgroundColor":"#ffffff","backgroundTransitions":{"enabled":false,"effects":[],"durationMs":80},
            """ + "\"backgroundAudioTriggerBeatTransition\":" + beat + "}"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var current = JsonDocument.Parse(await client.GetStringAsync($"/panel/devices/{id}"));
        var root = current.RootElement;
        Assert.Equal("#123456", root.GetProperty("backgroundColor").GetString());
        Assert.Equal(1700, root.GetProperty("backgroundTransitions").GetProperty("durationMs").GetInt32());
        Assert.False(root.TryGetProperty("backgroundAudioTriggerBeatTransition", out _));
    }
}
