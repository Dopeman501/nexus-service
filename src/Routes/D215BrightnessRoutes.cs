using Nexus.Service.Auth;
using Nexus.Service.Models;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.JungleLeopard;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

internal static class D215BrightnessRoutes
{
    private const string Path = "/panel/devices/{id}/d215-brightness";
    private const string Family = D215ConnectionWorker.HandlerId;

    internal static void MapD215BrightnessEndpoints(this WebApplication app)
    {
        app.MapGet(Path, (string id, HttpContext ctx, TokenService tokens, PanelDeviceRegistry registry,
            StreamedPanelCoordinator streams) =>
        {
            if (!HasDesktopBearer(ctx, tokens)) return Results.Unauthorized();

            var target = ResolveTarget(id, ctx.RequestServices, registry, streams);
            return target?.Session.ConfirmedBrightness is int confirmed
                ? Reply(new D215BrightnessResponse("confirmed", confirmed))
                : Unavailable();
        });

        app.MapPost(Path, async (string id, D215BrightnessRequest? body, HttpContext ctx, TokenService tokens,
            PanelDeviceRegistry registry, StreamedPanelCoordinator streams, CancellationToken ct) =>
        {
            if (!HasDesktopBearer(ctx, tokens)) return Results.Unauthorized();
            if (body?.Value is not int value || value is not (80 or 100))
            {
                return Results.Json(ApiResponse.Fail("D215 brightness must be 80 or 100"), AppJsonContext.Default.ApiResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var target = ResolveTarget(id, ctx.RequestServices, registry, streams);
            if (target is null) return Unavailable();

            var result = await target.Session.SetBrightnessAsync(value, target.IsAuthorized, ct).ConfigureAwait(false);
            return Reply(result);
        });
    }

    private static bool HasDesktopBearer(HttpContext ctx, TokenService tokens) =>
        ctx.Request.Headers.Authorization is { Count: 1 } headers
        && headers[0]?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
        && ServiceTokenRequests.HasServiceToken(ctx, tokens);

    private static Target? ResolveTarget(string id, IServiceProvider services, PanelDeviceRegistry registry, StreamedPanelCoordinator streams)
    {
        if (services.GetService<D215ConnectionWorker>() is not { } worker
            || streams.FindActivePanelStream(id, Family) is not { } stream
            || worker.GetIdentifiedSession(stream.Serial) is not { } session)
        {
            return null;
        }

        // Evaluate each owner separately. The callback must run outside the serial
        // session's own locks because the coordinator also reads transport state.
        bool IsAuthorized() => registry.Get(id)?.Capabilities is { Family: Family, Surface: PanelSurfaces.LcdPortrait or PanelSurfaces.LcdWide }
            && streams.FindActivePanelStream(id, Family) == stream
            && ReferenceEquals(worker.GetIdentifiedSession(stream.Serial), session);

        return IsAuthorized() ? new Target(session, IsAuthorized) : null;
    }

    private static IResult Unavailable() => Reply(new D215BrightnessResponse("unavailable", null));

    private static IResult Reply(D215BrightnessResponse response) =>
        Results.Json(response, AppJsonContext.Default.D215BrightnessResponse);

    private sealed record Target(D215Session Session, Func<bool> IsAuthorized);
}
