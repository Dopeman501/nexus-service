using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Media;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;

namespace Nexus.Service.Tests.Integration;

/// <summary>Measures the actual baked frames through the authenticated commit route.</summary>
public sealed class PanelBgFrameRateRouteTests : IDisposable
{
    // The bundled ffmpeg has no lavfi input; use a literal PNG image sequence.
    private const string PngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAYAAADED76LAAAAHUlEQVR4nGO8o6HxnwEJKN+4zojMZ0LmYAPDQwEAAEgEDtnh8EMAAAAASUVORK5CYII=";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-bg-fps-" + Guid.NewGuid().ToString("N"));
    private readonly PanelBgLibrary _library;
    private readonly NexusAppFactory _baseFactory;
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public PanelBgFrameRateRouteTests()
    {
        _library = new PanelBgLibrary(Path.Combine(_root, "library"));
        _baseFactory = new NexusAppFactory();
        _factory = _baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<PanelBgLibrary>();
            services.AddSingleton(_library);
        }));
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
    }

    [FfmpegTheory]
    [InlineData("jungle-leopard-d215", 60, 60)]
    [InlineData("jungle-leopard-d215", 24, 24)]
    [InlineData("jungle-leopard-d215", 30, 30)]
    [InlineData("jungle-leopard-d215", 120, 60)]
    [InlineData("other-panel", 60, 30)]
    [InlineData(null, 24, 30)]
    public async Task Commit_uses_the_registered_family_video_rate(string? family, int sourceFps, int expectedFps)
    {
        var registry = _factory.Services.GetRequiredService<PanelDeviceRegistry>();
        var deviceId = registry.Allocate("FPS test", new PanelDeviceCapabilities { Family = family }).Id;
        var source = await CreateOneSecondClipAsync(sourceFps);
        var stage = await PanelBgImporter.StageAsync(_library, deviceId, source, "test.mp4");
        Assert.True(stage.Ok, stage.Error);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(stage.StageId!), "stageId" },
            { new StringContent("0,0,1,1"), "crop" },
            { new StringContent("8"), "w" },
            { new StringContent("8"), "h" },
            // A submitted field cannot grant an unregistered panel a higher rate.
            { new StringContent("120"), "maxVideoFps" },
        };

        using var response = await _client.PostAsync($"/panel/devices/{deviceId}/background-media/commit", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<PanelBgImportResponse>();
        Assert.NotNull(result?.Item);
        Assert.InRange(result.Item.DurationSec, .95, 1.1);
        var output = _library.GetMediaPath(deviceId, result.Item.Id, ".mp4");
        var decoded = Path.Combine(_root, "decoded.rgb");
        await MediaImporter.RunFfmpeg("-y", "-i", output, "-fps_mode", "passthrough",
            "-f", "rawvideo", "-pix_fmt", "rgb24", decoded);
        var frameCount = new FileInfo(decoded).Length / (8 * 8 * 3);
        // FFmpeg may extend the last timestamp by up to two frames on conversion.
        Assert.InRange(frameCount, expectedFps, expectedFps + 2);
        Assert.InRange(frameCount / result.Item.DurationSec, expectedFps - .35, expectedFps + .35);
    }

    private async Task<string> CreateOneSecondClipAsync(int fps)
    {
        var frames = Path.Combine(_root, "frames");
        Directory.CreateDirectory(frames);
        var png = Convert.FromBase64String(PngBase64);
        for (var frame = 0; frame < fps; frame++)
            File.WriteAllBytes(Path.Combine(frames, $"frame-{frame:D3}.png"), png);
        var source = Path.Combine(_root, "source.mp4");
        await MediaImporter.RunFfmpeg("-y", "-framerate", fps.ToString(CultureInfo.InvariantCulture),
            "-i", Path.Combine(frames, "frame-%03d.png"), "-c:v", "libx264", "-pix_fmt", "yuv420p", source);
        return source;
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _baseFactory.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
