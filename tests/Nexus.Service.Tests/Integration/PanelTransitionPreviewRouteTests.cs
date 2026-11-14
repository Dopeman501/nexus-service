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

public sealed class PanelTransitionPreviewRouteTests : IClassFixture<PanelTransitionPreviewHostFactory>
{
    private readonly PanelTransitionPreviewHostFactory _factory;
    public PanelTransitionPreviewRouteTests(PanelTransitionPreviewHostFactory factory) => _factory = factory;
    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }
    private string Record(string surface = "lcd-wide") => _factory.Services.GetRequiredService<PanelDeviceRegistry>()
        .Allocate("Transition preview", new PanelDeviceCapabilities { Surface = surface }, canReuse: _ => false).Id;
    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");
    private static string Body(string session, long sequence, object? preview) => JsonSerializer.Serialize(new { sessionId = session, sequence, preview });
    private static object Selection(string? id = null) => new { previewId = id ?? Guid.NewGuid().ToString(), effect = "curtains:wave", durationMs = 1350 };
    private static Task<HttpResponseMessage> Post(HttpClient client, string id, string session, long sequence, object? preview) =>
        client.PostAsync($"/panel/devices/{id}/transition-preview", Json(Body(session, sequence, preview)));

    [Theory]
    [InlineData("lcd-round")]
    [InlineData("lcd-square")]
    [InlineData("lcd-wide")]
    [InlineData("lcd-portrait")]
    [InlineData("monitor")]
    [InlineData("y70")]
    public async Task Accepted_preview_broadcasts_one_shot_for_every_supported_local_surface_without_persisting(string surface)
    {
        using var client = Client();
        var id = Record(surface);
        var before = JsonSerializer.Serialize(_factory.Services.GetRequiredService<PanelDeviceRegistry>().Get(id));
        var hub = _factory.Services.GetRequiredService<MultiplexHub>();
        var frames = new List<JsonElement>();
        using var sub = hub.AddTestSubscription("panel/transition-preview");
        void Observe(string topic, ReadOnlyMemory<byte> payload) { if (topic == "panel/transition-preview") frames.Add(JsonDocument.Parse(payload).RootElement.Clone()); }
        hub.OnBroadcastForTest += Observe;
        try
        {
            using var response = await Post(client, id, Guid.NewGuid().ToString(), 1, Selection());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
            using var frame = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(7500, frame.RootElement.GetProperty("expiresAtUnixMs").GetInt64() - frame.RootElement.GetProperty("startedAtUnixMs").GetInt64() + 150);
            Assert.Contains(frames, item => item.GetProperty("d").GetProperty("deviceId").GetString() == id);
            Assert.Equal(before, JsonSerializer.Serialize(_factory.Services.GetRequiredService<PanelDeviceRegistry>().Get(id)));
        }
        finally { hub.OnBroadcastForTest -= Observe; }
    }

    [Fact]
    public async Task Repeated_click_id_does_not_restart_and_newer_editor_is_protected_from_late_cleanup()
    {
        using var client = Client();
        var id = Record();
        var session = Guid.NewGuid().ToString();
        var first = Selection();
        Assert.Equal(HttpStatusCode.OK, (await Post(client, id, session, 1, first)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(client, id, session, 2, first)).StatusCode);
        var next = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.OK, (await Post(client, id, next, 1, Selection())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(client, id, session, 3, null)).StatusCode);
        using var cleared = await Post(client, id, next, 2, null);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        using var frame = JsonDocument.Parse(await cleared.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, frame.RootElement.GetProperty("preview").ValueKind);
        Assert.Equal("null", await client.GetStringAsync($"/panel/devices/{id}/transition-preview"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"sessionId\":\"bad\",\"sequence\":1,\"preview\":null}")]
    [InlineData("{\"sessionId\":\"00000000-0000-0000-0000-000000000000\",\"sequence\":1,\"preview\":null}")]
    [InlineData("{\"sessionId\":\"53a3ec6e-25ab-428e-aad6-487ef1a33b7d\",\"sequence\":\"1\",\"preview\":null}")]
    [InlineData("{\"sessionId\":\"53a3ec6e-25ab-428e-aad6-487ef1a33b7d\",\"sequence\":-1,\"preview\":null}")]
    [InlineData("{\"sessionId\":\"53a3ec6e-25ab-428e-aad6-487ef1a33b7d\",\"sequence\":9007199254740992,\"preview\":null}")]
    [InlineData("{\"sessionId\":\"53a3ec6e-25ab-428e-aad6-487ef1a33b7d\",\"sequence\":1,\"preview\":{}}")]
    [InlineData("{\"sessionId\":\"53a3ec6e-25ab-428e-aad6-487ef1a33b7d\",\"sequence\":1,\"preview\":{\"previewId\":\"bad\",\"effect\":\"curtains:wave\",\"durationMs\":800}}")]
    [InlineData("{\"sessionId\":\"53a3ec6e-25ab-428e-aad6-487ef1a33b7d\",\"sequence\":1,\"preview\":{\"previewId\":\"53a3ec6e-25ab-428e-aad6-487ef1a33b7d\",\"effect\":\"motion:fade\",\"durationMs\":800}}")]
    [InlineData("{\"sessionId\":\"53a3ec6e-25ab-428e-aad6-487ef1a33b7d\",\"sequence\":1,\"preview\":{\"previewId\":\"53a3ec6e-25ab-428e-aad6-487ef1a33b7d\",\"effect\":\"none\",\"durationMs\":800}}")]
    [InlineData("{\"sessionId\":\"53a3ec6e-25ab-428e-aad6-487ef1a33b7d\",\"sequence\":1,\"preview\":{\"previewId\":\"53a3ec6e-25ab-428e-aad6-487ef1a33b7d\",\"effect\":\"curtains:wave\",\"durationMs\":\"800\"}}")]
    public async Task Malformed_preview_is_rejected_without_mutation(string body)
    {
        using var client = Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/panel/devices/{Record()}/transition-preview", Json(body))).StatusCode);
    }

    [Fact]
    public async Task Authentication_locality_and_supported_device_are_required_for_get_and_post()
    {
        using var client = Client();
        var session = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.NotFound, (await Post(client, "missing", session, 1, Selection())).StatusCode);
        var unsupported = Record("q60");
        Assert.Equal(HttpStatusCode.NotFound, (await Post(client, unsupported, session, 1, Selection())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/panel/devices/{unsupported}/transition-preview")).StatusCode);
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(anonymous, Record(), session, 1, Selection())).StatusCode);
        using var panel = TestPhoneSession.CreateClient(_factory);
        var id = Record("y70");
        Assert.Equal(HttpStatusCode.OK, (await Post(panel, id, session, 1, Selection())).StatusCode);
        foreach (var host in new[] { "localhost", "outside.example" })
        {
            var response = await _factory.Server.SendAsync(context =>
            {
                context.Connection.RemoteIpAddress = host == "localhost" ? IPAddress.Parse("192.168.1.50") : IPAddress.Loopback;
                context.Request.Method = "GET"; context.Request.Path = $"/panel/devices/{id}/transition-preview";
                context.Request.Host = new(host); context.Request.Headers.Authorization = panel.DefaultRequestHeaders.Authorization!.ToString();
            });
            Assert.Equal(404, response.Response.StatusCode);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Declared_and_chunked_bodies_over_8k_are_rejected(bool declared)
    {
        using var client = Client();
        var body = Body(Guid.NewGuid().ToString(), 1, new { previewId = Guid.NewGuid(), effect = new string('x', 8192), durationMs = 800 });
        using HttpContent content = declared ? Json(body) : new ChunkedJsonContent(body);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/panel/devices/{Record()}/transition-preview", content)).StatusCode);
    }

    [Fact]
    public async Task Request_budget_rejects_before_reading_and_recovers_at_the_monotonic_boundary()
    {
        var clock = new RequestClock();
        using var factory = new PanelTransitionPreviewHostFactory { Clock = clock };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Services.GetRequiredService<TokenService>().Token);
        var id = factory.Services.GetRequiredService<PanelDeviceRegistry>().Allocate("Rate limit", new() { Surface = "lcd-wide" }).Id;
        var path = $"/panel/devices/{id}/transition-preview";
        for (var i = 0; i < 256; i++) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
        using var body = new CountingStream(Encoding.UTF8.GetBytes("{}"));
        var response = await factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            context.Request.Method = "POST"; context.Request.Path = path; context.Request.Host = new("localhost");
            context.Request.ContentType = "application/json"; context.Request.ContentLength = body.Length; context.Request.Body = body;
            context.Request.Headers.Authorization = client.DefaultRequestHeaders.Authorization!.ToString();
        });
        Assert.Equal(429, response.Response.StatusCode);
        Assert.Equal("1", response.Response.Headers.RetryAfter);
        Assert.Equal(0, body.BytesRead);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync(path)).StatusCode);
        clock.Milliseconds = 1000;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
    }

    private sealed class ChunkedJsonContent(string body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(Encoding.UTF8.GetBytes(body)).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal long BytesRead;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await base.ReadAsync(buffer, cancellationToken); BytesRead += count; return count;
        }
    }
    private sealed class RequestClock : TimeProvider
    {
        internal long Milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }
}

public sealed class PanelTransitionPreviewHostFactory : NexusAppFactory
{
    internal TimeProvider Clock { get; init; } = TimeProvider.System;
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<RgbBridge>(); services.RemoveAll<OpenRgbProcessManager>();
            services.RemoveAll<IBeatsProvider>(); services.AddSingleton<IBeatsProvider, StubBeatsProvider>();
            services.RemoveAll<ILightingDeviceProvider>();
            services.AddSingleton<ILightingDeviceProvider>(sp => new StubDeviceProvider(sp.GetRequiredService<IConfigStore>()));
            services.RemoveAll<PanelTransitionPreviewStore>(); services.AddSingleton(new PanelTransitionPreviewStore(Clock));
        });
    }
}
