using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Devices;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.JungleLeopard;

namespace Nexus.Service.Tests.Integration;

public sealed class D215BrightnessRouteTests : IDisposable
{
    private const string Family = "jungle-leopard-d215";
    private readonly NexusAppFactory _baseFactory = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakePort _port = new();

    public D215BrightnessRouteTests()
    {
        _factory = _baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IStreamedPanelDiscovery>();
            services.RemoveAll<D215ConnectionWorker>();
            services.RemoveAll<StreamedPanelStore>();
            services.AddSingleton(new StreamedPanelStore(Path.ChangeExtension(_baseFactory.SettingsPath, ".streams.json")));
            services.AddSingleton(sp => new D215ConnectionWorker(new FakeDiscovery(), sp.GetRequiredService<DeviceControlGate>(),
                (_, _) => ValueTask.FromResult<ID215SerialTransport>(_port), TimeProvider.System,
                D215Timing.Default with { PortSettle = TimeSpan.Zero, ClearSettle = TimeSpan.Zero }, TimeSpan.FromMilliseconds(20)));
            services.AddSingleton<IStreamedPanelDiscovery, D215PanelDiscovery>();
        }));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _baseFactory.Dispose();
    }

    private PanelDeviceRegistry Registry => _factory.Services.GetRequiredService<PanelDeviceRegistry>();

    private HttpClient DesktopClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    [Fact]
    public async Task Phone_and_query_tokens_cannot_authorize_either_operation()
    {
        using var phone = TestPhoneSession.CreateClient(_factory);
        const string route = "/panel/devices/d215-test/d215-brightness";
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.PostAsJsonAsync(route, new { value = 80 })).StatusCode);

        using var query = _factory.CreateClient();
        var queryRoute = route + "?token=" + Uri.EscapeDataString(_factory.Services.GetRequiredService<TokenService>().Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await query.GetAsync(queryRoute)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await query.PostAsJsonAsync(queryRoute, new { value = 80 })).StatusCode);
    }

    [Theory]
    [InlineData("{\"value\":0}")]
    [InlineData("{\"value\":19}")]
    [InlineData("{\"value\":21}")]
    [InlineData("{\"value\":39}")]
    [InlineData("{\"value\":41}")]
    [InlineData("{\"value\":59}")]
    [InlineData("{\"value\":61}")]
    [InlineData("{\"value\":101}")]
    [InlineData("{\"value\":20.5}")]
    [InlineData("{\"value\":90}")]
    [InlineData("{\"value\":null}")]
    [InlineData("{}")]
    [InlineData("{\"value\":\"80\"}")]
    [InlineData("{\"value\":80,\"port\":\"COM1\"}")]
    public async Task Only_numeric_presets_are_accepted(string body)
    {
        using var client = DesktopClient();
        using var response = await client.PostAsync("/panel/devices/unknown/d215-brightness",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_saved_D215_record_and_another_family_cannot_target_the_wire()
    {
        using var client = DesktopClient();
        var stale = Registry.Allocate("Stored D215", D215Capabilities(), canReuse: _ => false);
        var other = Registry.Allocate("Other LCD", new PanelDeviceCapabilities { Surface = "lcd-wide" }, canReuse: _ => false);

        foreach (var id in new[] { stale.Id, other.Id, "missing" })
        {
            var route = $"/panel/devices/{id}/d215-brightness";
            await AssertBrightness(await client.GetAsync(route), "unavailable", null);
            await AssertBrightness(await client.PostAsJsonAsync(route, new { value = 100 }), "unavailable", null);
        }
        Assert.Equal(0, _port.BrightnessWrites);
    }

    [Fact]
    public async Task Generic_patch_cannot_persist_a_D215_brightness_setting()
    {
        using var client = DesktopClient();
        var device = Registry.Allocate("Stored D215", D215Capabilities(), canReuse: _ => false);
        var response = await client.PostAsJsonAsync($"/panel/devices/{device.Id}", new { lcdBrightness = 80 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(Registry.Get(device.Id)?.LcdBrightness);
        Assert.Null(Registry.Patch(device.Id, new PanelDevicePatch { LcdBrightness = 100 })?.LcdBrightness);
    }

    [Fact]
    public async Task Generic_wide_allocation_does_not_reuse_the_D215_record()
    {
        using var client = DesktopClient();
        var d215 = Registry.Allocate("Wide D215",
            new PanelDeviceCapabilities { Family = Family, Surface = "lcd-wide" }, canReuse: _ => false);
        var body = new { capabilities = new { surface = "lcd-wide" } };
        using var first = await client.PostAsJsonAsync("/panel/devices", body);
        using var second = await client.PostAsJsonAsync("/panel/devices", body);
        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();
        var generic = await first.Content.ReadFromJsonAsync<PanelDeviceRecord>();
        var reused = await second.Content.ReadFromJsonAsync<PanelDeviceRecord>();
        Assert.NotNull(generic);
        Assert.NotEqual(d215.Id, generic.Id);
        Assert.Equal(generic.Id, reused?.Id);
        Assert.Equal(Family, Registry.Get(d215.Id)?.Capabilities?.Family);
    }

    [Theory]
    [InlineData(20, false, "confirmed", 20)]
    [InlineData(40, false, "confirmed", 40)]
    [InlineData(60, false, "confirmed", 60)]
    [InlineData(80, false, "confirmed", 80)]
    [InlineData(100, false, "confirmed", 100)]
    [InlineData(100, true, "mismatch", 80)]
    public async Task Active_record_returns_the_device_readback_without_persisting_it(int value, bool ignoreWrite, string status, int readback)
    {
        using var client = DesktopClient();
        var worker = _factory.Services.GetRequiredService<D215ConnectionWorker>();
        var streams = _factory.Services.GetRequiredService<StreamedPanelCoordinator>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            var record = await AttachAsync(worker, streams, deadline.Token);
            var route = $"/panel/devices/{record}/d215-brightness";
            var forged = Registry.Allocate("Unbound D215", D215Capabilities(), canReuse: _ => false);
            await AssertBrightness(await client.PostAsJsonAsync($"/panel/devices/{forged.Id}/d215-brightness", new { value = 100 }),
                "unavailable", null);

            await AssertBrightness(await client.GetAsync(route), "confirmed", 80);
            _port.IgnoreBrightnessWrite = ignoreWrite;
            await AssertBrightness(await client.PostAsJsonAsync(route, new { value }), status, readback);
            var expectedWrites = value == 80 ? 0 : 1;
            Assert.Equal(expectedWrites, _port.BrightnessWrites);
            Assert.Null(Registry.Get(record)?.LcdBrightness);

            Assert.True(Registry.Remove(record));
            await AssertBrightness(await client.PostAsJsonAsync(route, new { value = 80 }), "unavailable", null);
            Assert.Equal(expectedWrites, _port.BrightnessWrites);
        }
        finally
        {
            await streams.StopAsync(CancellationToken.None);
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Losing_the_panel_record_during_preflight_prevents_the_brightness_write()
    {
        using var client = DesktopClient();
        var worker = _factory.Services.GetRequiredService<D215ConnectionWorker>();
        var streams = _factory.Services.GetRequiredService<StreamedPanelCoordinator>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        FakePort.InfoPause? pause = null;
        try
        {
            var record = await AttachAsync(worker, streams, deadline.Token);
            pause = _port.PauseNextInfo();
            var response = client.PostAsJsonAsync($"/panel/devices/{record}/d215-brightness", new { value = 100 }, deadline.Token);
            await pause.Entered.Task.WaitAsync(deadline.Token);
            Assert.True(Registry.Remove(record));
            pause.Resume.TrySetResult();

            await AssertBrightness(await response, "unavailable", null);
            Assert.Equal(0, _port.BrightnessWrites);
        }
        finally
        {
            pause?.Resume.TrySetResult();
            await streams.StopAsync(CancellationToken.None);
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task All_four_rotations_persist_atomically_without_replacing_the_panel_or_lease()
    {
        using var client = DesktopClient();
        var worker = _factory.Services.GetRequiredService<D215ConnectionWorker>();
        var streams = _factory.Services.GetRequiredService<StreamedPanelCoordinator>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            var record = await AttachAsync(worker, streams, deadline.Token);
            var lease = worker.GetIdentifiedSession("itest-d215");
            foreach (var (portrait, flip180, width, height) in new[]
            {
                (false, false, 1680, 756), (false, true, 1680, 756),
                (true, false, 756, 1680), (true, true, 756, 1680),
            })
            {
                using var response = await client.PostAsJsonAsync($"/panel/devices/{record}",
                    new { portrait, flip180 }, deadline.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var saved = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
                Assert.Equal(portrait, saved.RootElement.GetProperty("portrait").GetBoolean());
                Assert.Equal(flip180, saved.RootElement.GetProperty("flip180").GetBoolean());

                streams.TickOnce();
                var assignment = Assert.Single(streams.GetAssignments().Assignments);
                Assert.Equal(record, assignment.PanelDeviceId);
                Assert.Equal((width, height), (assignment.CssWidth, assignment.CssHeight));
                Assert.Equal(portrait, Registry.Get(record)?.Portrait);
                Assert.Equal(flip180, Registry.Get(record)?.Flip180);
                Assert.Same(lease, worker.GetIdentifiedSession("itest-d215"));
            }
        }
        finally
        {
            await streams.StopAsync(CancellationToken.None);
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Frame_cap_changes_remint_the_renderer_at_up_to_sixty_without_replacing_the_panel_or_lease()
    {
        using var client = DesktopClient();
        var worker = _factory.Services.GetRequiredService<D215ConnectionWorker>();
        var streams = _factory.Services.GetRequiredService<StreamedPanelCoordinator>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            var record = await AttachAsync(worker, streams, deadline.Token);
            var lease = worker.GetIdentifiedSession("itest-d215");
            var previous = Assert.Single(streams.GetAssignments().Assignments);
            Assert.Equal(60, previous.Fps);
            foreach (var (cap, expected) in new[] { (15, 15), (1, 1), (60, 60), (5, 5), (0, 60), (120, 60) })
            {
                using var response = await client.PostAsJsonAsync($"/panel/devices/{record}", new { fpsCap = cap }, deadline.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                streams.TickOnce();
                var current = Assert.Single(streams.GetAssignments().Assignments);
                Assert.Equal(expected, current.Fps);
                Assert.Equal(record, current.PanelDeviceId);
                Assert.Same(lease, worker.GetIdentifiedSession("itest-d215"));
                if (previous.Fps != expected) Assert.NotEqual(previous.SessionId, current.SessionId);
                Assert.Equal(cap == 0 ? null : (int?)cap, Registry.Get(record)?.FpsCap);
                previous = current;
            }
            using var lower = await client.PostAsJsonAsync($"/panel/devices/{record}", new { fpsCap = 15 }, deadline.Token);
            Assert.Equal(HttpStatusCode.OK, lower.StatusCode);
            var gate = _factory.Services.GetRequiredService<DeviceControlGate>();
            gate.SetEnabled(Family, false);
            while (worker.Snapshot.Status != D215ConnectionStatus.Available) await Task.Delay(10, deadline.Token);
            streams.TickOnce();
            Assert.Empty(streams.GetAssignments().Assignments);
            gate.SetEnabled(Family, true);
            while (worker.Snapshot.Status != D215ConnectionStatus.Identified) await Task.Delay(10, deadline.Token);
            streams.TickOnce();
            var reconnected = Assert.Single(streams.GetAssignments().Assignments);
            Assert.Equal(record, reconnected.PanelDeviceId);
            Assert.Equal(15, reconnected.Fps);
            Assert.NotSame(lease, worker.GetIdentifiedSession("itest-d215"));
        }
        finally
        {
            await streams.StopAsync(CancellationToken.None);
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Orientation_restarts_the_stream_and_keeps_the_record_playlist_and_brightness_route()
    {
        using var client = DesktopClient();
        var worker = _factory.Services.GetRequiredService<D215ConnectionWorker>();
        var streams = _factory.Services.GetRequiredService<StreamedPanelCoordinator>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            var record = await AttachAsync(worker, streams, deadline.Token);
            var lease = Assert.IsType<D215Session>(worker.GetIdentifiedSession("itest-d215"));
            var portrait = Assert.Single(streams.GetAssignments().Assignments);
            Assert.Equal((756, 1680, 60, "rawBgra"), (portrait.CssWidth, portrait.CssHeight, portrait.Fps, portrait.Codec));
            Assert.Null(Registry.Get(record)?.Portrait);
            var route = $"/panel/devices/{record}";
            string[] playlist = ["second-media", "first-media"];
            using var landscapePatch = await client.PostAsJsonAsync(route,
                new { portrait = false, backgroundMediaOrder = playlist, themeMode = "dark" });
            Assert.Equal(HttpStatusCode.OK, landscapePatch.StatusCode);

            streams.TickOnce();
            var landscape = Assert.Single(streams.GetAssignments().Assignments);
            Assert.Equal(record, landscape.PanelDeviceId);
            Assert.Same(lease, worker.GetIdentifiedSession("itest-d215"));
            Assert.NotEqual(portrait.SessionId, landscape.SessionId);
            Assert.Equal((1680, 756, 60, "rawBgra"), (landscape.CssWidth, landscape.CssHeight, landscape.Fps, landscape.Codec));
            var wideRecord = Assert.IsType<PanelDeviceRecord>(Registry.Get(record));
            Assert.Equal("lcd-wide", wideRecord.Capabilities?.Surface);
            Assert.True(wideRecord.Capabilities?.SupportsPortrait);
            Assert.Equal(1680, wideRecord.Capabilities?.CssWidth);
            Assert.Equal(756, wideRecord.Capabilities?.CssHeight);
            Assert.Equal(playlist, wideRecord.BackgroundMediaOrder);
            Assert.Equal("dark", wideRecord.ThemeMode);

            using var forged = await client.PostAsJsonAsync("/panel/devices",
                new { capabilities = new { family = Family, surface = "lcd-wide" } });
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
            using var capabilitiesPatch = await client.PostAsJsonAsync(route,
                new { capabilities = new { family = "other", surface = "lcd-wide" } });
            Assert.Equal(HttpStatusCode.BadRequest, capabilitiesPatch.StatusCode);
            using var genericBrightnessPatch = await client.PostAsJsonAsync(route, new { lcdBrightness = 100 });
            Assert.Equal(HttpStatusCode.BadRequest, genericBrightnessPatch.StatusCode);
            await AssertBrightness(await client.GetAsync(route + "/d215-brightness"), "confirmed", 80);
            await AssertBrightness(await client.PostAsJsonAsync(route + "/d215-brightness", new { value = 100 }), "confirmed", 100);

            using var portraitPatch = await client.PostAsJsonAsync(route, new { portrait = true });
            Assert.Equal(HttpStatusCode.OK, portraitPatch.StatusCode);
            streams.TickOnce();
            var restored = Assert.Single(streams.GetAssignments().Assignments);
            Assert.Equal(record, restored.PanelDeviceId);
            Assert.Same(lease, worker.GetIdentifiedSession("itest-d215"));
            Assert.NotEqual(landscape.SessionId, restored.SessionId);
            Assert.Equal((756, 1680, 60), (restored.CssWidth, restored.CssHeight, restored.Fps));
            Assert.Equal("lcd-portrait", Registry.Get(record)?.Capabilities?.Surface);
            Assert.Equal(playlist, Registry.Get(record)?.BackgroundMediaOrder);
        }
        finally
        {
            await streams.StopAsync(CancellationToken.None);
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private async Task<string> AttachAsync(D215ConnectionWorker worker, StreamedPanelCoordinator streams, CancellationToken ct)
    {
        _factory.Services.GetRequiredService<DeviceControlGate>().SetEnabled(Family, true);
        await worker.StartAsync(ct);
        while (worker.Snapshot.Status != D215ConnectionStatus.Identified)
            await Task.Delay(10, ct);
        streams.TickOnce();
        return Assert.Single(streams.GetAssignments().Assignments).PanelDeviceId;
    }

    private static PanelDeviceCapabilities D215Capabilities() => new()
    {
        Family = Family, Surface = "lcd-portrait", CssWidth = 756, CssHeight = 1680,
    };

    private static async Task AssertBrightness(HttpResponseMessage response, string expectedStatus, int? expectedValue)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(expectedStatus, result.RootElement.GetProperty("status").GetString());
            var value = result.RootElement.GetProperty("value");
            if (expectedValue.HasValue) Assert.Equal(expectedValue.Value, value.GetInt32());
            else Assert.Equal(JsonValueKind.Null, value.ValueKind);
        }
    }

    private sealed class FakeDiscovery : ID215PortDiscovery
    {
        public IReadOnlyList<D215Port> Discover() => [new D215Port("COM7", "itest-d215", true)];
    }

    private sealed class FakePort : ID215SerialTransport
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private int _brightness = 80;
        private int _brightnessWrites;
        private InfoPause? _nextInfoPause;
        public int BrightnessWrites => Volatile.Read(ref _brightnessWrites);
        public volatile bool IgnoreBrightnessWrite;

        internal InfoPause PauseNextInfo()
        {
            var pause = new InfoPause();
            Volatile.Write(ref _nextInfoPause, pause);
            return pause;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = source.Span;
            if (bytes.Length < 7 || bytes[0] != 0x55 || bytes[1] != 0xAA) return ValueTask.CompletedTask;
            switch (bytes[4])
            {
                case 0x06:
                    return ReplyToInfoAsync(cancellationToken);
                case 0x03:
                    if (!IgnoreBrightnessWrite) _brightness = bytes[5];
                    Interlocked.Increment(ref _brightnessWrites);
                    _replies.Writer.TryWrite(Frame(0x03, [0]));
                    break;
                case 0x11:
                    _replies.Writer.TryWrite(Frame(0x11, [0]));
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected command 0x{bytes[4]:X2}");
            }
            return ValueTask.CompletedTask;
        }

        private async ValueTask ReplyToInfoAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _nextInfoPause, null) is { } pause)
            {
                pause.Entered.TrySetResult();
                await pause.Resume.Task.WaitAsync(ct);
            }
            _replies.Writer.TryWrite(Frame(0x06, Encoding.UTF8.GetBytes(
                "{\"cmd\":\"info\",\"data\":{\"model\":\"D215-NOR-JD9365-6.67inch-hor\",\"version\":\"3.4\",\"brightness\":" + _brightness + ",\"diplay_on\":true}}")));
        }

        public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            var bytes = await _replies.Reader.ReadAsync(cancellationToken);
            bytes.AsSpan().CopyTo(destination.Span);
            return bytes.Length;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        internal sealed class InfoPause
        {
            internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static byte[] Frame(byte command, ReadOnlySpan<byte> payload)
        {
            var frame = new byte[7 + payload.Length];
            frame[0] = 0x55;
            frame[1] = 0xAA;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), (ushort)frame.Length);
            frame[4] = command;
            payload.CopyTo(frame.AsSpan(5));
            ushort checksum = 0;
            foreach (var value in frame.AsSpan(0, frame.Length - 2)) checksum += value;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(frame.Length - 2), checksum);
            return frame;
        }
    }
}
