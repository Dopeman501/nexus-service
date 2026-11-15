using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.KeyReactive;
using Nexus.Service.Models;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

/// <summary>Key reactions: per-keyboard config, simulated presses, and the dashboard's preview loop.</summary>
public static class KeyReactiveRoutes
{
    private const int MaxKeyLength = 32;

    public static void MapKeyReactiveEndpoints(this WebApplication app)
    {
        app.MapGet("/lighting/key-reactive", (HttpContext context, KeyReactiveOverlay overlay, LightingEngine engine, IConfigStore store) =>
        {
            var response = new KeyReactiveStateResponse { InputAvailable = overlay.InputAvailable?.Invoke() == true };
            var frames = engine.Devices;
            store.Read(settings =>
            {
                response.Epoch = settings.AudioTriggerControlEpoch;
                foreach (var frame in frames)
                {
                    if (!KeyReactiveOverlay.IsKeyboard(frame)) continue;
                    var config = ConfigFor(settings, frame);
                    var geometry = KeyReactiveOverlay.GeometryOf(frame, config);
                    response.Devices.Add(new KeyReactiveDeviceDto
                    {
                        Id = frame.Id,
                        DeviceId = KeyReactiveOverlay.ConfigKey(frame),
                        FrameIndex = frame.Index,
                        HardwareKeys = overlay.HasHardwareKeySource(KeyReactiveOverlay.ConfigKey(frame)),
                        LedCount = frame.LedCount,
                        NamedKeys = geometry?.NamedKeys ?? 0,
                        ZoneLighting = frame.ReactionLayout is not null,
                        SupportedEffects = frame.ReactionLayout is null ? [.. KeyReactionCatalog.Effects] : [.. K55CoreReactionLayout.SupportedEffects],
                        KeyboardLayouts = frame.ReactionLayout is null ? [] : [.. K55CoreReactionLayout.KeyboardLayouts],
                        LayoutRevision = KeyReactiveOverlay.LayoutRevisionOf(frame),
                        Config = config,
                    });
                }
            });
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(response, AppJsonContext.Default.KeyReactiveStateResponse);
        });

        // Takes a card id and stores under its device, so every card of a board shares one config.
        app.MapPut("/lighting/key-reactive/{id}", (string id, string? expectedEpoch, string? expectedLayoutRevision,
            KeyReaction body, IConfigStore store, LightingEngine engine) =>
        {
            if (!ValidEpoch(expectedEpoch)) return InvalidEpoch();
            if (!K55CoreReactionLayout.ValidRevision(expectedLayoutRevision)) return InvalidRevision();
            if (FindKeyboard(engine, id) is not { } frame) return NotAKeyboard();
            if (frame.ReactionLayout is not null && body.Enabled && !K55CoreReactionLayout.IsKnownLayout(body.KeyboardLayout))
                return LayoutRequired();
            KeyReaction? saved = null;
            var applied = store.TryUpdate(settings =>
            {
                if (!MatchesEpoch(settings, expectedEpoch)) return false;
                var current = FindKeyboard(engine, id);
                if (current is null || !MatchesRevision(current, expectedLayoutRevision)
                    || KeyReactiveOverlay.ConfigKey(current) != KeyReactiveOverlay.ConfigKey(frame)
                    || current.ReactionLayout is not null && body.Enabled && !K55CoreReactionLayout.IsKnownLayout(body.KeyboardLayout))
                {
                    return false;
                }
                saved = KeyReactionCatalog.SanitizeForDevice(current, body);
                settings.Lighting.KeyReactions[KeyReactiveOverlay.ConfigKey(current)] = saved;
                return true;
            });
            return applied ? Results.Json(saved!, AppJsonContext.Default.KeyReaction) : ScopeChanged();
        });

        app.MapPost("/lighting/key-reactive/{id}/press", (string id, string? expectedEpoch, string? expectedLayoutRevision, KeyReactivePressBody? body,
            IConfigStore store, KeyReactiveOverlay overlay, LightingEngine engine) =>
        {
            if (!ValidEpoch(expectedEpoch)) return InvalidEpoch();
            if (!K55CoreReactionLayout.ValidRevision(expectedLayoutRevision)) return InvalidRevision();
            if (FindKeyboard(engine, id) is not { } frame) return NotAKeyboard();
            var key = body?.Key;
            if (key is { Length: > MaxKeyLength }) return Results.BadRequest(ApiResponse.Fail("key too long"));
            var accepted = false;
            var layoutMissing = false;
            store.Read(settings =>
            {
                if (!MatchesEpoch(settings, expectedEpoch)) return;
                var current = FindKeyboard(engine, id);
                if (current is null || !MatchesRevision(current, expectedLayoutRevision)
                    || KeyReactiveOverlay.ConfigKey(current) != KeyReactiveOverlay.ConfigKey(frame))
                {
                    return;
                }
                layoutMissing = current.ReactionLayout is not null && !K55CoreReactionLayout.IsKnownLayout(ConfigFor(settings, current).KeyboardLayout);
                if (layoutMissing) return;
                accepted = overlay.TryPressOn(current, string.IsNullOrWhiteSpace(key) ? null : key, body?.Led,
                    settings.AudioTriggerControlEpoch, expectedLayoutRevision);
            });
            return accepted ? Results.Ok(ApiResponse.Ok()) : layoutMissing ? LayoutRequired() : ScopeChanged();
        });

        app.MapPost("/lighting/key-reactive/{id}/preview", (string id, string? expectedEpoch, string? expectedLayoutRevision,
            KeyReaction body, IConfigStore store, LightingEngine engine) =>
        {
            if (!ValidEpoch(expectedEpoch)) return InvalidEpoch();
            if (!K55CoreReactionLayout.ValidRevision(expectedLayoutRevision)) return InvalidRevision();
            if (FindKeyboard(engine, id) is not { } frame) return NotAKeyboard();
            var currentScope = false;
            store.Read(settings =>
            {
                if (!MatchesEpoch(settings, expectedEpoch)) return;
                var current = FindKeyboard(engine, id);
                if (current is null || !MatchesRevision(current, expectedLayoutRevision)
                    || KeyReactiveOverlay.ConfigKey(current) != KeyReactiveOverlay.ConfigKey(frame))
                {
                    return;
                }
                frame = current;
                currentScope = true;
            });
            if (!currentScope) return ScopeChanged();
            var config = KeyReactionCatalog.SanitizeForDevice(frame, body);
            if (frame.ReactionLayout is not null && !K55CoreReactionLayout.IsKnownLayout(config.KeyboardLayout))
                return LayoutRequired();
            if (KeyReactiveOverlay.GeometryOf(frame, config) is not { } geo) return NotAKeyboard();
            return Results.Json(KeyReactionPreview.Render(geo, config), AppJsonContext.Default.KeyReactivePreviewResponse);
        });
    }

    private static DeviceFrame? FindKeyboard(LightingEngine engine, string id)
    {
        foreach (var frame in engine.Devices)
        {
            if (frame.Id == id) return KeyReactiveOverlay.IsKeyboard(frame) ? frame : null;
        }
        return null;
    }

    private static IResult NotAKeyboard() =>
        Results.NotFound(ApiResponse.Fail("not a reactive keyboard"));

    private static IResult LayoutRequired() => Results.BadRequest(ApiResponse.Fail("select a supported physical keyboard layout"));

    private static KeyReaction ConfigFor(NexusSettings settings, DeviceFrame frame) =>
        KeyReactionCatalog.SanitizeForDevice(frame, settings.Lighting.KeyReactions.GetValueOrDefault(KeyReactiveOverlay.ConfigKey(frame)));

    private static bool ValidEpoch(string? epoch) => epoch is null || epoch.Length == 32 && epoch.All(char.IsAsciiHexDigit);

    // ProfileManager already rotates this runtime generation atomically with profile application.
    private static bool MatchesEpoch(NexusSettings settings, string? expected) => expected is null || expected == settings.AudioTriggerControlEpoch;
    private static bool MatchesRevision(DeviceFrame frame, string? expected) => expected is null || expected == KeyReactiveOverlay.LayoutRevisionOf(frame);

    private static IResult InvalidEpoch() => Results.BadRequest(ApiResponse.Fail("invalid profile epoch"));
    private static IResult InvalidRevision() => Results.BadRequest(ApiResponse.Fail("invalid keyboard layout revision"));
    private static IResult ScopeChanged() => Results.Conflict(ApiResponse.Fail("key reactive profile or device changed"));
}
