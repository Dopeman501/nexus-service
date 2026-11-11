using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Activity;
using Nexus.Service.Auth;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Models.Devices;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

public sealed class AudioTriggerTargetsRouteTests : IClassFixture<AudioTriggerTargetsHostFactory>
{
    private readonly AudioTriggerTargetsHostFactory _factory;
    private readonly HttpClient _client;
    private readonly IConfigStore _store;
    public AudioTriggerTargetsRouteTests(AudioTriggerTargetsHostFactory factory)
    {
        _factory = factory;
        factory.ResetSettings();
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<TokenService>().Token);
        _store = factory.Services.GetRequiredService<IConfigStore>();
        _store.Update(settings =>
        {
            settings.PanelDevices["panel"] = new() { Id = "panel", DisplayName = "Y70", BackgroundAudioTrigger = new()
                { Enabled = true, Intensity = 60, Backgrounds = [new() { Mode = "solid", Color = "#ff0000" }] } };
            settings.Lighting.AudioTrigger = new() { Enabled = true, Looks = [new() { Mode = "static", Color = "#00ff00" }] };
        });
    }

    private Task<HttpResponseMessage> Post(string json) => _client.PostAsync("/audio-trigger/targets", new StringContent(json, Encoding.UTF8, "application/json"));

    [Fact]
    public async Task Narrow_and_all_updates_round_trip_through_real_serialization_and_preserve_unrelated_settings()
    {
        var sync = _store.Load().Lighting.Sync;
        var response = await Post("""{"targetIds":["lighting:fan"],"enabled":false,"intensity":85}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(3, body.RootElement.GetProperty("targets").GetArrayLength());
        Assert.Equal(85, _store.Load().Lighting.AudioTrigger!.DeviceOverrides!["fan"].Intensity);
        Assert.True(_store.Load().Lighting.AudioTrigger!.Enabled);
        Assert.True(_store.Load().PanelDevices["panel"].BackgroundAudioTrigger!.Enabled);
        response = await Post("""{"enabled":false,"intensity":20}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var all = JsonDocument.Parse(await _client.GetStringAsync("/audio-trigger/targets"));
        Assert.All(all.RootElement.GetProperty("targets").EnumerateArray(), target =>
        {
            Assert.False(target.GetProperty("enabled").GetBoolean());
            Assert.Equal(20, target.GetProperty("intensity").GetDouble());
        });
        Assert.Equal(sync, _store.Load().Lighting.Sync);
        Assert.Single(_store.Load().Lighting.AudioTrigger!.Looks);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("""{"intensity":"NaN"}""")]
    [InlineData("""{"intensity":101}""")]
    [InlineData("""{"intensity":-1}""")]
    [InlineData("""{"enabled":false,"targetIds":[]}""")]
    [InlineData("""{"enabled":false,"targetIds":["background:panel","background:panel"]}""")]
    [InlineData("""{"enabled":false,"targetIds":[null]}""")]
    public async Task Invalid_patches_are_rejected_without_writes(string json)
    {
        var response = await Post(json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(_store.Load().PanelDevices["panel"].BackgroundAudioTrigger!.Enabled);
    }

    [Fact]
    public async Task Unknown_targets_reject_the_whole_batch_and_oversized_ignored_json_is_bounded()
    {
        var changes = 0;
        void Changed() => changes++;
        _store.OnChanged += Changed;
        var response = await Post("""{"targetIds":["background:panel","lighting:missing"],"enabled":false}""");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(_store.Load().PanelDevices["panel"].BackgroundAudioTrigger!.Enabled);
        Assert.Equal(0, changes);
        _store.OnChanged -= Changed;
        response = await Post("{\"enabled\":false,\"ignored\":\"" + new string('x', 400 * 1024) + "\"}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(_store.Load().PanelDevices["panel"].BackgroundAudioTrigger!.Enabled);
    }

    [Fact]
    public async Task Stale_epoch_returns_conflict_without_writes_and_look_only_editor_preserves_newer_master_controls()
    {
        using var before = JsonDocument.Parse(await _client.GetStringAsync("/audio-trigger/targets"));
        var old = before.RootElement.GetProperty("epoch").GetString();
        _store.Update(settings => settings.AudioTriggerControlEpoch = Guid.NewGuid().ToString("N"));
        var changes = 0;
        void Changed() => changes++;
        _store.OnChanged += Changed;
        var response = await Post("{\"enabled\":false,\"expectedEpoch\":\"" + old + "\"}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, changes);
        _store.OnChanged -= Changed;
        await Post("""{"intensity":80}""");
        await Post("""{"targetIds":["lighting:fan"],"intensity":90}""");
        response = await _client.PostAsync("/lighting/audio-trigger?controls=", new StringContent(
            """{"enabled":false,"intensity":50,"looks":[{"mode":"static","color":"#0000ff"}]}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(_store.Load().Lighting.AudioTrigger!.Enabled);
        Assert.Equal(80, _store.Load().Lighting.AudioTrigger!.Intensity);
        Assert.Equal(90, _store.Load().Lighting.AudioTrigger!.DeviceOverrides!["fan"].Intensity);
    }

    [Fact]
    public async Task Editor_control_mask_rejects_unknown_fields_and_changes_only_deliberately_selected_master_control()
    {
        await Post("""{"intensity":80}""");
        var json = """{"enabled":false,"intensity":50,"looks":[{"mode":"static","color":"#0000ff"}]}""";
        var response = await _client.PostAsync("/lighting/audio-trigger?controls=unknown", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(_store.Load().Lighting.AudioTrigger!.Enabled);
        response = await _client.PostAsync("/lighting/audio-trigger?controls=enabled", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(_store.Load().Lighting.AudioTrigger!.Enabled);
        Assert.Equal(80, _store.Load().Lighting.AudioTrigger!.Intensity);
    }

    [Fact]
    public async Task Authentication_is_required_for_both_operations()
    {
        using var unpaired = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await unpaired.GetAsync("/audio-trigger/targets")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await unpaired.PostAsync("/audio-trigger/targets", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
    }

    [Fact]
    public async Task Stale_shared_editor_cannot_discard_device_controls_but_global_intensity_can_reset_them()
    {
        await Post("""{"targetIds":["lighting:fan"],"intensity":90}""");
        var response = await _client.PostAsync("/lighting/audio-trigger", new StringContent(
            """{"enabled":true,"intensity":50,"looks":[{"mode":"static","color":"#0000ff"}]}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(90, _store.Load().Lighting.AudioTrigger!.DeviceOverrides!["fan"].Intensity);
        response = await _client.PostAsync("/lighting/audio-trigger", new StringContent(
            """{"enabled":true,"intensity":30,"looks":[{"mode":"static","color":"#0000ff"}]}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(_store.Load().Lighting.AudioTrigger!.DeviceOverrides!["fan"].Intensity);
    }
}

public sealed class AudioTriggerTargetsHostFactory : NexusAppFactory
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
            services.RemoveAll<ILightingDeviceProvider>();
            services.AddSingleton<ILightingDeviceProvider, AudioTriggerTestDevices>();
        });
    }
}

public sealed class AudioTriggerTestDevices : ILightingDeviceProvider
{
    public bool IsConnected => true;
    public GetLightingDevicesResponse GetAll() => new()
    {
        IsInit = true, Devices = [new() { Id = "fan", Name = "Fan", LedCount = 10 }, new() { Id = "ram", Name = "RAM", LedCount = 8 }],
    };
    public void SetDisabled(IReadOnlyList<string> ids) { }
    public void SetPower(string id, bool on) { }
    public void SetBrightness(string id, int brightness) { }
    public void SetHue(string id, float hue) { }
    public void SetSaturation(string id, float saturation) { }
    public void SetZoneLedCount(string id, int count) { }
    public void Identify(string id, int durationMs) { }
}
