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
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests.Integration;

public sealed class PanelAudioTriggerPreviewRouteTests : IClassFixture<PanelAudioTriggerPreviewHostFactory>
{
    private readonly PanelAudioTriggerPreviewHostFactory _factory;

    public PanelAudioTriggerPreviewRouteTests(PanelAudioTriggerPreviewHostFactory factory) => _factory = factory;

    [Fact]
    public void Fixture_cannot_resolve_hardware_RGB_owners_and_uses_stub_devices_and_audio()
    {
        Assert.Null(_factory.Services.GetService<RgbBridge>());
        Assert.Null(_factory.Services.GetService<OpenRgbProcessManager>());
        Assert.IsType<StubDeviceProvider>(_factory.Services.GetRequiredService<ILightingDeviceProvider>());
        Assert.IsType<StubBeatsProvider>(_factory.Services.GetRequiredService<IBeatsProvider>());
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private string Record(string surface = "lcd-wide") => _factory.Services.GetRequiredService<PanelDeviceRegistry>()
        .Allocate("Preview", new PanelDeviceCapabilities { Surface = surface }, canReuse: _ => false).Id;

    private static Task<HttpResponseMessage> Post(HttpClient client, string id, string session, long sequence, string background) =>
        client.PostAsync($"/panel/devices/{id}/audio-trigger-preview", new StringContent(
            $$"""{"sessionId":"{{session}}","sequence":{{sequence}},"background":{{background}}}""", Encoding.UTF8, "application/json"));

    [Fact]
    public async Task Shared_request_budget_rejects_get_and_post_before_reading_and_recovers_after_a_second()
    {
        var clock = new RequestClock();
        using var factory = new PanelAudioTriggerPreviewHostFactory { Clock = clock };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Services.GetRequiredService<TokenService>().Token);
        var id = factory.Services.GetRequiredService<PanelDeviceRegistry>()
            .Allocate("Rate limit", new PanelDeviceCapabilities { Surface = "lcd-wide" }, canReuse: _ => false).Id;
        var path = $"/panel/devices/{id}/audio-trigger-preview";
        for (var request = 0; request < 256; request++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);

        using var body = new CountingStream(Encoding.UTF8.GetBytes("{}"));
        var rejected = await factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Request.Method = "POST";
            context.Request.Path = path;
            context.Request.Host = new("localhost");
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = body.Length;
            context.Request.Body = body;
            context.Request.Headers.Authorization = client.DefaultRequestHeaders.Authorization!.ToString();
        });
        Assert.Equal(429, rejected.Response.StatusCode);
        Assert.Equal("1", rejected.Response.Headers.RetryAfter);
        Assert.Equal(0, body.BytesRead);
        using var throttledGet = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttledGet.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), throttledGet.Headers.RetryAfter!.Delta);
        using var unknownDevice = await client.GetAsync("/panel/devices/missing-preview-record/audio-trigger-preview");
        Assert.Equal(HttpStatusCode.TooManyRequests, unknownDevice.StatusCode);
        clock.Milliseconds = 1000;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData("lcd-round")]
    [InlineData("lcd-square")]
    [InlineData("lcd-wide")]
    [InlineData("lcd-portrait")]
    [InlineData("monitor")]
    [InlineData("y70")]
    public async Task Selection_is_transient_on_every_audio_capable_surface_and_broadcasts_the_accepted_frame(string surface)
    {
        using var client = Client();
        var id = Record(surface);
        var registry = _factory.Services.GetRequiredService<PanelDeviceRegistry>();
        var before = JsonSerializer.Serialize(registry.Get(id));
        var hub = _factory.Services.GetRequiredService<MultiplexHub>();
        var frames = new List<JsonElement>();
        using var sub = hub.AddTestSubscription("panel/audio-trigger-preview");
        void Observe(string topic, ReadOnlyMemory<byte> payload)
        {
            if (topic == "panel/audio-trigger-preview")
                frames.Add(JsonDocument.Parse(payload).RootElement.Clone());
        }
        hub.OnBroadcastForTest += Observe;
        try
        {
            var session = Guid.NewGuid().ToString();
            using var response = await Post(client, id, session, 0, """{"mode":"solid","color":"#abcdef"}""");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var frame = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(id, frame.RootElement.GetProperty("deviceId").GetString());
            Assert.Equal(session, frame.RootElement.GetProperty("sessionId").GetString());
            Assert.Equal(0, frame.RootElement.GetProperty("sequence").GetInt64());
            Assert.Equal("#abcdef", frame.RootElement.GetProperty("background").GetProperty("color").GetString());
            Assert.InRange(frame.RootElement.GetProperty("expiresAtUnixMs").GetInt64(),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 4000, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 6500);
            using var current = JsonDocument.Parse(await client.GetStringAsync($"/panel/devices/{id}/audio-trigger-preview"));
            Assert.Equal(frame.RootElement.GetProperty("revision").GetInt64(), current.RootElement.GetProperty("revision").GetInt64());
            Assert.Contains(frames, item => item.GetProperty("d").GetProperty("deviceId").GetString() == id);
            Assert.Equal(before, JsonSerializer.Serialize(registry.Get(id)));
        }
        finally { hub.OnBroadcastForTest -= Observe; }
    }

    [Fact]
    public async Task Newer_editor_wins_and_old_updates_or_cleanup_cannot_replace_it()
    {
        using var client = Client();
        var id = Record();
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.OK, (await Post(client, id, first, 1, """{"mode":"solid","color":"#ff0000"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(client, id, first, 0, """{"mode":"solid","color":"#ffffff"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(client, id, second, 0, """{"mode":"solid","color":"#00ff00"}""")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(client, id, first, 2, "null")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(client, id, first, 3, """{"mode":"solid","color":"#ff0000"}""")).StatusCode);
        using var clear = await Post(client, id, second, 1, "null");
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        using var clearedFrame = JsonDocument.Parse(await clear.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, clearedFrame.RootElement.GetProperty("background").ValueKind);
        Assert.Equal("null", await client.GetStringAsync($"/panel/devices/{id}/audio-trigger-preview"));
        Assert.Equal(HttpStatusCode.Conflict, (await Post(client, id, second, 0, """{"mode":"solid","color":"#00ff00"}""")).StatusCode);
    }

    [Theory]
    [InlineData("bad", 0, "null")]
    [InlineData("00000000-0000-0000-0000-000000000000", 0, "null")]
    [InlineData("53a3ec6e-25ab-428e-aad6-487ef1a33b7d", -1, "null")]
    [InlineData("53a3ec6e-25ab-428e-aad6-487ef1a33b7d", 9007199254740992, "null")]
    [InlineData("53a3ec6e-25ab-428e-aad6-487ef1a33b7d", 0, "{}")]
    [InlineData("53a3ec6e-25ab-428e-aad6-487ef1a33b7d", 0, "{\"mode\":\"solid\",\"color\":\"url(bad)\"}")]
    [InlineData("53a3ec6e-25ab-428e-aad6-487ef1a33b7d", 0, "{\"mode\":\"media\"}")]
    [InlineData("53a3ec6e-25ab-428e-aad6-487ef1a33b7d", 0, "{\"mode\":\"solid\",\"slideshow\":true}")]
    public async Task Invalid_preview_is_rejected(string session, long sequence, string background)
    {
        using var client = Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, Record(), session, sequence, background)).StatusCode);
    }

    [Fact]
    public async Task Unknown_unsupported_and_unauthenticated_requests_are_rejected()
    {
        using var client = Client();
        var session = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.NotFound, (await Post(client, "missing", session, 0, "null")).StatusCode);
        var unsupported = Record("q60");
        Assert.Equal(HttpStatusCode.NotFound, (await Post(client, unsupported, session, 0, "null")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/panel/devices/{unsupported}/audio-trigger-preview")).StatusCode);
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(anonymous, Record(), session, 0, "null")).StatusCode);
    }

    [Fact]
    public async Task Local_panel_sessions_can_preview_but_remote_sessions_cannot()
    {
        using var panel = TestPhoneSession.CreateClient(_factory);
        var id = Record("y70");
        var session = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.OK, (await Post(panel, id, session, 0, """{"mode":"solid","color":"#abcdef"}""")).StatusCode);
        var response = await _factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.50");
            context.Request.Method = "GET";
            context.Request.Path = $"/panel/devices/{id}/audio-trigger-preview";
            context.Request.Host = new("localhost");
            context.Request.Headers.Authorization = panel.DefaultRequestHeaders.Authorization!.ToString();
        });
        Assert.Equal(404, response.Response.StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"sessionId\":\"53a3ec6e-25ab-428e-aad6-487ef1a33b7d\",\"sequence\":\"0\",\"background\":null}")]
    public async Task Missing_body_fields_and_string_sequences_are_rejected(string body)
    {
        using var client = Client();
        using var response = await client.PostAsync($"/panel/devices/{Record()}/audio-trigger-preview",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Oversized_declared_or_chunked_unknown_fields_are_rejected_before_deserialization(bool declared)
    {
        using var client = Client();
        var session = Guid.NewGuid().ToString();
        var padding = new string('x', 64 * 1024);
        var body = $$"""{"sessionId":"{{session}}","sequence":0,"background":{"mode":"solid","color":"#abcdef"},"unknown":"{{padding}}"}""";
        using HttpContent content = declared ? new StringContent(body, Encoding.UTF8, "application/json") : new ChunkedJsonContent(body);
        using var response = await client.PostAsync($"/panel/devices/{Record()}/audio-trigger-preview", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Oversized_declared_known_field_is_rejected_without_reading_the_body()
    {
        using var client = Client();
        var id = Record();
        var json = JsonSerializer.Serialize(new { sessionId = Guid.NewGuid(), sequence = 0,
            background = new { mode = "solid", color = new string('a', 64 * 1024) } });
        using var body = new CountingStream(Encoding.UTF8.GetBytes(json));
        var response = await _factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Request.Method = "POST";
            context.Request.Path = $"/panel/devices/{id}/audio-trigger-preview";
            context.Request.Host = new("localhost");
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = body.Length;
            context.Request.Body = body;
            context.Request.Headers.Authorization = client.DefaultRequestHeaders.Authorization!.ToString();
        });
        Assert.Equal(400, response.Response.StatusCode);
        Assert.Equal(0, body.BytesRead);
    }

    private sealed class ChunkedJsonContent : HttpContent
    {
        private readonly string _body;
        internal ChunkedJsonContent(string body) { _body = body; Headers.ContentType = new("application/json"); }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(Encoding.UTF8.GetBytes(_body)).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal long BytesRead;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }
    }

    private sealed class RequestClock : TimeProvider
    {
        internal long Milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }
}

/// <summary>Preview route checks need no actuator, process manager or real capture source.</summary>
public sealed class PanelAudioTriggerPreviewHostFactory : NexusAppFactory
{
    internal TimeProvider Clock { get; init; } = TimeProvider.System;

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
            services.AddSingleton<ILightingDeviceProvider>(sp =>
                new StubDeviceProvider(sp.GetRequiredService<IConfigStore>()));
            services.RemoveAll<PanelAudioTriggerPreviewStore>();
            services.AddSingleton(new PanelAudioTriggerPreviewStore(Clock));
        });
    }
}
