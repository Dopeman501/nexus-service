using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Features;
using Nexus.Service.Auth;
using Nexus.Service.Diagnostics;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

internal static class EfficiencyDiagnosticRoutes
{
    internal static void MapEfficiencyDiagnosticEndpoints(this WebApplication app)
    {
        app.MapGet("/diagnostics/efficiency/session", (HttpContext ctx, TokenService tokens, EfficiencyDiagnosticSession owner) =>
        {
            if (!Authorized(ctx, tokens)) return Results.Unauthorized();
            if (!owner.AcceptRequest()) return Results.StatusCode(429);
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Json(owner.Snapshot(), AppJsonContext.Default.EfficiencyLeaseProjection);
        }).LocalhostOnly();
        MapMutation(app, "/diagnostics/efficiency/session", cancel: false);
        MapMutation(app, "/diagnostics/efficiency/session/cancel", cancel: true);
        app.MapPost("/diagnostics/efficiency/session/ack", async (HttpContext ctx, TokenService tokens, EfficiencyDiagnosticSession owner) =>
        {
            if (!Authorized(ctx, tokens)) return Results.Unauthorized();
            if (!owner.AcceptRequest()) return Results.StatusCode(429);
            var body = await Read(ctx, AppJsonContext.Default.EfficiencyLeaseAck);
            return Results.StatusCode(body is null ? 400 : owner.Acknowledge(body));
        }).LocalhostOnly();
    }

    private static void MapMutation(WebApplication app, string route, bool cancel)
    {
        app.MapPost(route, async (HttpContext ctx, TokenService tokens, EfficiencyDiagnosticSession owner) =>
        {
            if (!Authorized(ctx, tokens)) return Results.Unauthorized();
            if (!owner.AcceptRequest()) return Results.StatusCode(429);
            var body = await Read(ctx, AppJsonContext.Default.EfficiencyLeaseRequest);
            if (body is null) return Results.StatusCode(400);
            EfficiencyLeaseProjection state;
            var status = cancel ? owner.Cancel(body, out state) : owner.Start(body, out state);
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Json(state, AppJsonContext.Default.EfficiencyLeaseProjection, statusCode: status);
        }).LocalhostOnly();
    }

    internal static bool Authorized(HttpContext ctx, TokenService tokens)
    {
        if (ctx.Request.Headers.Authorization is not { Count: 1 } auth
            || auth[0]?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) != true
            || !ServiceTokenRequests.HasServiceToken(ctx, tokens)) return false;
        var origin = ctx.Request.Headers.Origin;
        if (origin.Count == 0) return true;
        return origin.Count == 1 && Uri.TryCreate(origin[0], UriKind.Absolute, out var uri)
            && uri.Scheme == ctx.Request.Scheme && uri.Authority.Equals(ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
            && string.IsNullOrEmpty(uri.UserInfo);
    }

    private static async Task<T?> Read<T>(HttpContext ctx, JsonTypeInfo<T> type)
    {
        const int maximum = 1024;
        if (ctx.Request.ContentLength > maximum) return default;
        var size = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (size is { IsReadOnly: false }) size.MaxRequestBodySize = maximum;
        try
        {
            var (bytes, tooLarge) = await CloudRoutes.ReadBoundedAsync(ctx.Request.Body, maximum, ctx.RequestAborted);
            return tooLarge || bytes is null ? default : JsonSerializer.Deserialize(bytes, type);
        }
        catch (Exception error) when (error is JsonException or BadHttpRequestException) { return default; }
    }
}
