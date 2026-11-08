using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Nexus.Service.Activity;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

public sealed class LightingAudioTriggerRouteTests : IClassFixture<LightingAudioTriggerHostFactory>
{
    private readonly LightingAudioTriggerHostFactory _factory;
    private readonly HttpClient _client;

    public LightingAudioTriggerRouteTests(LightingAudioTriggerHostFactory factory)
    {
        _factory = factory;
        _factory.ResetSettings();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _factory.Services.GetRequiredService<TokenService>().Token);
    }

    private Task<HttpResponseMessage> Post(string json) => _client.PostAsync("/lighting/audio-trigger",
        new StringContent(json, Encoding.UTF8, "application/json"));

    [Fact]
    public void Fixture_cannot_resolve_a_hardware_RGB_bridge_or_process_manager_and_audio_is_stubbed()
    {
        Assert.Null(_factory.Services.GetService<RgbBridge>());
        Assert.Null(_factory.Services.GetService<OpenRgbProcessManager>());
        Assert.IsType<StubBeatsProvider>(_factory.Services.GetRequiredService<IBeatsProvider>());
    }

    [Fact]
    public async Task Default_off_then_round_trips_independently_of_normal_lighting_and_music_reactive()
    {
        using var before = JsonDocument.Parse(await _client.GetStringAsync("/lighting/audio-trigger"));
        Assert.False(before.RootElement.GetProperty("enabled").GetBoolean());
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        var sync = store.Load().Lighting.Sync;
        var music = store.Load().Lighting.MusicReactive;
        var response = await Post("""{"enabled":true,"intensity":65,"switchDelay":0.4,"shuffle":true,"looks":[{"mode":"static","color":"#FF0000"},{"mode":"animate","effect":"plasma"}]}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var after = JsonDocument.Parse(await _client.GetStringAsync("/lighting/audio-trigger"));
        Assert.True(after.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal("#ff0000", after.RootElement.GetProperty("looks")[0].GetProperty("color").GetString());
        Assert.Equal(2, store.Load().Lighting.AudioTrigger!.Looks.Count);
        Assert.Equal(sync, store.Load().Lighting.Sync);
        Assert.Equal(music, store.Load().Lighting.MusicReactive);
    }

    [Fact]
    public async Task Media_choice_round_trips_without_selecting_shared_media_or_normal_mode()
    {
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        store.Update(settings => settings.Lighting.LastMediaId = "normal-clip");
        var sync = store.Load().Lighting.Sync;
        var response = await Post("""{"enabled":true,"looks":[{"mode":"media","mediaId":"trigger-clip"}]}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var after = JsonDocument.Parse(await _client.GetStringAsync("/lighting/audio-trigger"));
        Assert.Equal("trigger-clip", after.RootElement.GetProperty("looks")[0].GetProperty("mediaId").GetString());
        Assert.Equal("normal-clip", store.Load().Lighting.LastMediaId);
        Assert.Equal(sync, store.Load().Lighting.Sync);
    }

    [Theory]
    [InlineData("""{"intensity":101}""")]
    [InlineData("""{"switchDelay":-1}""")]
    [InlineData("""{"looks":[{"mode":"animate","effect":"unknown"}]}""")]
    [InlineData("""{"looks":[{"mode":"static","color":"url(external)"}]}""")]
    [InlineData("""{"looks":[{"mode":"animate","effect":"plasma","state":{"intensity":2}}]}""")]
    [InlineData("""{"looks":null}""")]
    [InlineData("""{"looks":[{"mode":"media","mediaId":"../clip"}]}""")]
    [InlineData("""{"looks":[{"mode":"media","mediaId":"clip","state":{}}]}""")]
    public async Task Invalid_input_is_rejected_without_changing_settings(string json)
    {
        var response = await Post(json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_factory.Services.GetRequiredService<IConfigStore>().Load().Lighting.AudioTrigger);
    }

    [Fact]
    public async Task Authentication_is_required()
    {
        using var unpaired = _factory.CreateClient();
        var response = await unpaired.PostAsync("/lighting/audio-trigger", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

/// <summary>Test-host boot gates do not prevent route-triggered hardware activation; remove those owners too.</summary>
public sealed class LightingAudioTriggerHostFactory : NexusAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<RgbBridge>();
            services.RemoveAll<OpenRgbProcessManager>();
            services.RemoveAll<IBeatsProvider>();
            services.AddSingleton<IBeatsProvider, StubBeatsProvider>();
        });
    }
}
