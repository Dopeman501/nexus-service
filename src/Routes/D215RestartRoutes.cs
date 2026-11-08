using System.Linq;
using Nexus.Service.Auth;
using Nexus.Service.Models;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.JungleLeopard;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

internal static class D215RestartRoutes
{
    private const string Family = D215ConnectionWorker.HandlerId;

    internal static void MapD215RestartEndpoints(this WebApplication app)
    {
        app.MapPost("/panel/devices/{id}/d215-restart", async (string id, D215RestartRequest? body, HttpContext ctx,
            TokenService tokens, PanelDeviceRegistry registry, StreamedPanelStore store,
            StreamedPanelCoordinator streams, CancellationToken ct) =>
        {
            if (ctx.Request.Headers.Authorization is not { Count: 1 } headers
                || headers[0]?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) != true
                || !ServiceTokenRequests.HasServiceToken(ctx, tokens))
            {
                return Results.Unauthorized();
            }
            if (body is null)
            {
                return Results.Json(ApiResponse.Fail("An empty restart request is required"), AppJsonContext.Default.ApiResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }
            var worker = ctx.RequestServices.GetService<D215ConnectionWorker>();
            var identity = ResolveIdentity(id, registry, store, streams);
            if (worker is null || identity is null) return Reply(new("unavailable"));
            bool IsAuthorized() => ResolveIdentity(id, registry, store, streams) == identity;
            return Reply(await worker.RestartAsync(identity, IsAuthorized, ct).ConfigureAwait(false));
        });
    }

    private static string? ResolveIdentity(string id, PanelDeviceRegistry registry, StreamedPanelStore store,
        StreamedPanelCoordinator streams)
    {
        if (registry.Get(id)?.Capabilities is not { Family: Family, Surface: PanelSurfaces.LcdPortrait or PanelSurfaces.LcdWide })
            return null;
        var stored = store.Load().Where(pair => pair.Value.PanelDeviceId == id).Select(pair => pair.Key).ToArray();
        if (stored.Length != 1) return null;
        var active = streams.FindActivePanelStream(id, Family);
        return active is null || active.Value.Serial == stored[0] ? stored[0] : null;
    }

    private static IResult Reply(D215RestartResponse result) => Results.Json(result, AppJsonContext.Default.D215RestartResponse);
}
