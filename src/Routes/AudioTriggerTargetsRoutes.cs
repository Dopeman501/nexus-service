using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Nexus.Service.Auth;
using Nexus.Service.Devices;
using Nexus.Service.Lighting;
using Nexus.Service.Models;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

internal static class AudioTriggerTargetsRoutes
{
    private const int MaximumBodyBytes = 384 * 1024;

    internal static void Map(WebApplication app)
    {
        var budget = new AudioTriggerRequestBudget();
        app.MapGet("/audio-trigger/targets", (HttpContext context, IConfigStore store, ILightingDeviceProvider devices) =>
        {
            if (!budget.TryAccept()) return RateLimited(context);
            var hardware = devices.GetAll().Devices;
            AudioTriggerTargetsResponse snapshot = new();
            store.Read(settings => snapshot = AudioTriggerTargets.Snapshot(settings, hardware));
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(snapshot, AppJsonContext.Default.AudioTriggerTargetsResponse);
        }).AllowPanel();

        app.MapPost("/audio-trigger/targets", async (HttpContext context, IConfigStore store, ILightingDeviceProvider devices, MultiplexHub hub) =>
        {
            if (!budget.TryAccept()) return RateLimited(context);
            var body = await ReadBody(context);
            if (!AudioTriggerTargets.ValidPatch(body)) return Failure("invalid_audio_trigger_controls", 400);
            var hardware = devices.GetAll().Devices;
            AudioTriggerTargetsResponse snapshot = new();
            var applied = store.TryUpdate(settings =>
            {
                if (!AudioTriggerTargets.Apply(settings, hardware, body!)) return false;
                snapshot = AudioTriggerTargets.Snapshot(settings, hardware);
                return true;
            });
            if (!applied) return Failure("audio_trigger_target_unavailable", 409);
            foreach (var target in snapshot.Targets.Where(target => target.Kind == "background"
                && (body!.TargetIds is null || body.TargetIds.Contains(target.Id))))
            {
                PanelTopics.BroadcastPanelDevice(hub, target.Id[11..]);
            }
            PanelTopics.BroadcastLighting(hub);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(snapshot, AppJsonContext.Default.AudioTriggerTargetsResponse);
        }).AllowPanel();
    }

    private static IResult Failure(string message, int status) => Results.Json(ApiResponse.Fail(message),
        AppJsonContext.Default.ApiResponse, statusCode: status);

    private static IResult RateLimited(HttpContext context)
    {
        context.Response.Headers.RetryAfter = "1";
        context.Response.Headers.CacheControl = "no-store";
        return Failure("audio_trigger_controls_rate_limited", 429);
    }

    private static async Task<AudioTriggerTargetsPatch?> ReadBody(HttpContext context)
    {
        if (context.Request.ContentLength > MaximumBodyBytes) return null;
        var size = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (size is { IsReadOnly: false }) size.MaxRequestBodySize = MaximumBodyBytes;
        try
        {
            var (bytes, tooLarge) = await CloudRoutes.ReadBoundedAsync(context.Request.Body, MaximumBodyBytes, context.RequestAborted);
            return tooLarge || bytes is null ? null : JsonSerializer.Deserialize(bytes, AppJsonContext.Default.AudioTriggerTargetsPatch);
        }
        catch (Exception error) when (error is JsonException or BadHttpRequestException) { return null; }
    }
}
