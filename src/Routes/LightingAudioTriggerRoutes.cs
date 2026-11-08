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
        app.MapPost("/lighting/audio-trigger", (LightingAudioTriggerSettings body, IConfigStore store, MultiplexHub hub) =>
        {
            if (!LightingAudioTriggerPolicy.IsValid(body))
            {
                return Results.Json(ApiResponse.Fail("Invalid Audio Trigger settings"), AppJsonContext.Default.ApiResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }
            var value = LightingAudioTriggerPolicy.Clone(body)!;
            store.Update(settings =>
            {
                settings.Lighting.AudioTrigger = value;
                LightingPresetLooks.CaptureIntoActive(settings);
            });
            PanelTopics.BroadcastLighting(hub);
            return Results.Json(LightingAudioTriggerPolicy.Clone(value)!, AppJsonContext.Default.LightingAudioTriggerSettings);
        }).AllowPanel();
    }
}
