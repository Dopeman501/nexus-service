using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Nexus.Service.Auth;
using Nexus.Service.Models;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

internal static class PanelAudioTriggerPreviewRoutes
{
    internal const string Topic = "panel/audio-trigger-preview";
    private const int MaximumBodyBytes = 64 * 1024;

    internal static void MapPanelAudioTriggerPreviewEndpoints(this WebApplication app)
    {
        app.MapGet("/panel/devices/{id}/audio-trigger-preview", (string id, HttpContext context,
            PanelDeviceRegistry registry, PanelAudioTriggerPreviewStore previews) =>
        {
            if (!IsLocal(context)) return Results.NotFound();
            if (!previews.TryAcceptRequest()) return RateLimited(context);
            if (!SupportsPreview(registry.Get(id))) return Results.NotFound();
            context.Response.Headers.CacheControl = "no-store";
            var frame = previews.Get(id);
            return frame is null ? Results.Text("null", "application/json")
                : Results.Json(frame, AppJsonContext.Default.PanelAudioTriggerPreviewFrame);
        }).AllowPanel();

        app.MapPost("/panel/devices/{id}/audio-trigger-preview", async (string id,
            HttpContext context, PanelDeviceRegistry registry, PanelAudioTriggerPreviewStore previews, MultiplexHub hub) =>
        {
            if (!IsLocal(context)) return Results.NotFound();
            if (!previews.TryAcceptRequest()) return RateLimited(context);
            if (!SupportsPreview(registry.Get(id))) return Results.NotFound();
            var body = await ReadBodyAsync(context);
            if (!PanelAudioTriggerPreviewStore.IsValid(body))
                return Results.Json(ApiResponse.Fail("invalid_audio_trigger_preview"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            if (!previews.TryUpdate(id, body!, out var frame))
                return Results.Json(ApiResponse.Fail("audio_trigger_preview_superseded"), AppJsonContext.Default.ApiResponse, statusCode: 409);
            if (hub.TopicHasSubscribers(Topic))
            {
                using var publication = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                publication.CancelAfter(TimeSpan.FromSeconds(1));
                await hub.BroadcastTopicAsync(Topic, WsEnvelope.Build(Topic, frame!, AppJsonContext.Default.PanelAudioTriggerPreviewFrame), publication.Token);
            }
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(frame, AppJsonContext.Default.PanelAudioTriggerPreviewFrame);
        }).AllowPanel();
    }

    private static IResult RateLimited(HttpContext context)
    {
        context.Response.Headers.RetryAfter = "1";
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(ApiResponse.Fail("audio_trigger_preview_rate_limited"), AppJsonContext.Default.ApiResponse, statusCode: 429);
    }

    private static async Task<PanelAudioTriggerPreviewRequest?> ReadBodyAsync(HttpContext context)
    {
        if (context.Request.ContentLength > MaximumBodyBytes) return null;
        var size = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (size is { IsReadOnly: false }) size.MaxRequestBodySize = MaximumBodyBytes;
        try
        {
            var (bytes, tooLarge) = await CloudRoutes.ReadBoundedAsync(context.Request.Body, MaximumBodyBytes, context.RequestAborted);
            return tooLarge || bytes is null ? null
                : JsonSerializer.Deserialize(bytes, AppJsonContext.Default.PanelAudioTriggerPreviewRequest);
        }
        catch (Exception error) when (error is JsonException or BadHttpRequestException)
        {
            return null;
        }
    }

    private static bool IsLocal(HttpContext context) => AuthRequestPolicy.IsLoopbackRemote(context)
        && AuthRequestPolicy.IsLocalHostHeader(context);

    // Keep aligned with nexus-web panelAudioReactiveAllowed; no remote or Q-series capture/preview.
    private static bool SupportsPreview(PanelDeviceRecord? record) => PanelSurfaces.Canonical(record?.Capabilities?.Surface)
        is PanelSurfaces.Y70 or PanelSurfaces.Monitor or PanelSurfaces.LcdRound or PanelSurfaces.LcdSquare
            or PanelSurfaces.LcdWide or PanelSurfaces.LcdPortrait;
}
