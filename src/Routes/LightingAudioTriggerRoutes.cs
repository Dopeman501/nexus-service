using Nexus.Service.Auth;
using Nexus.Service.Lighting;
using Nexus.Service.Models;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

internal static class LightingAudioTriggerRoutes
{
    internal static void Map(WebApplication app)
    {
        app.MapGet("/lighting/audio-trigger", (IConfigStore store) =>
        {
            LightingAudioTriggerSettings? value = null;
            store.Read(settings => value = LightingAudioTriggerPolicy.Clone(settings.Lighting.AudioTrigger));
            return Results.Json(value ?? new(), AppJsonContext.Default.LightingAudioTriggerSettings);
        }).AllowPanel();
        app.MapPost("/lighting/audio-trigger", (LightingAudioTriggerSettings body, HttpContext context, IConfigStore store, MultiplexHub hub) =>
        {
            var hasControlFields = context.Request.Query.ContainsKey("controls");
            var controlFields = context.Request.Query["controls"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            if (controlFields.Any(field => field is not ("enabled" or "intensity")))
                return Results.Json(ApiResponse.Fail("Invalid Audio Trigger controls"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            if (!LightingAudioTriggerPolicy.IsValid(body))
            {
                return Results.Json(ApiResponse.Fail("Invalid Audio Trigger settings"), AppJsonContext.Default.ApiResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }
            var value = LightingAudioTriggerPolicy.Clone(body)!;
            var applied = store.TryUpdate(settings =>
            {
                // This editor owns shared looks and master controls. Per-card controls belong to
                // /audio-trigger/targets, so an older open editor must not replace their newer values.
                value.DeviceOverrides = LightingAudioTriggerPolicy.Clone(settings.Lighting.AudioTrigger)?.DeviceOverrides;
                if (hasControlFields)
                {
                    var current = settings.Lighting.AudioTrigger ?? new();
                    if (!controlFields.Contains("enabled")) value.Enabled = current.Enabled;
                    if (!controlFields.Contains("intensity")) value.Intensity = current.Intensity;
                }
                if (settings.Lighting.AudioTrigger is { } previous && previous.Intensity != value.Intensity
                    && value.DeviceOverrides is { } controls)
                {
                    value.DeviceOverrides = controls.ToDictionary(pair => pair.Key,
                        pair => new LightingAudioTriggerDeviceOverride { Enabled = pair.Value.Enabled }, StringComparer.Ordinal);
                }
                if (!LightingAudioTriggerPolicy.IsValid(value)) return false;
                settings.Lighting.AudioTrigger = value;
                LightingPresetLooks.CaptureIntoActive(settings);
                return true;
            });
            if (!applied)
            {
                return Results.Json(ApiResponse.Fail("Invalid Audio Trigger settings"),
                    AppJsonContext.Default.ApiResponse, statusCode: 400);
            }
            PanelTopics.BroadcastLighting(hub);
            return Results.Json(LightingAudioTriggerPolicy.Clone(value)!, AppJsonContext.Default.LightingAudioTriggerSettings);
        }).AllowPanel();
    }
}
