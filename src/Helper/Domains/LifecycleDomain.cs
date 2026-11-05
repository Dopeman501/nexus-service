#if WINDOWS
using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

// ---------- Wire messages + AppJsonContext entries ----------

namespace Nexus.Service.Helper.Domains;

/// <summary>
/// Payload for <c>helper.shutdown</c>. Service-to-helper. The service
/// fires this from its ApplicationStopping hook so the helper closes
/// its --app window (Edge --app shell) and exits, matching the
/// settings "Stop Nexus" UX. No fields - the envelope's existence is
/// the signal.
/// </summary>
public sealed class HelperShutdownPayload { }

/// <summary>
/// Payload for <c>overlay.prefsChanged</c>. Service-to-helper, one-way.
/// The service fires this on every settings.json change so the helper
/// can PostMessage the nexus-overlay marshaler to repoll preferences
/// without waiting for its 5 s timer. The service itself cannot post
/// to the overlay window directly because it runs in Session 0; the
/// helper runs in the user session where FindWindow can see the
/// marshaler.
/// </summary>
public sealed class OverlayPrefsChangedPayload { }

/// <summary>
/// Payload for <c>service.requestStop</c>. Helper-to-service. Fired
/// when the user clicks "Shut down" in the tray. The service handler
/// calls IHostApplicationLifetime.StopApplication so the daemon runs
/// the same graceful shutdown the /service/stop HTTP route uses.
/// Going over the already-authenticated pipe avoids needing to widen
/// the service's SCM DACL with SERVICE_STOP for the interactive user.
/// No fields.
/// </summary>
public sealed class ServiceRequestStopPayload { }

// JSON source-gen registration is centralised in
// src/Serialization/AppJsonContext.cs to avoid hintName collisions in
// .NET's JsonSourceGenerator when the same partial class is declared
// across many files. When you add a new payload type here, append a
// matching [JsonSerializable(typeof(YourPayload))] line to AppJsonContext.

// ---------- Service-side commands (outbound) ----------

/// <summary>
/// Service-side outbound facade for lifecycle commands. Service code
/// calls these to push lifecycle signals at the helper.
/// </summary>
[SupportedOSPlatform("windows")]
public static class LifecycleCommands
{
    public static async Task SendShutdownAsync(HelperRegistry registry, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null) return;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await conn.SendAsync(
                type: "helper.shutdown",
                payload: new HelperShutdownPayload(),
                payloadType: AppJsonContext.Default.HelperShutdownPayload,
                ct: budget.Token).ConfigureAwait(false);
            // A pipe write can finish before the helper reads the command.
            await conn.Closed.WaitAsync(budget.Token).ConfigureAwait(false);
            ServiceLog.Info($"[helper-pipe] connection closed during shutdown (session {conn.SessionId})");
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            ServiceLog.Warn($"[helper-pipe] shutdown wait ended before connection close (session {conn.SessionId})");
            throw;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            ServiceLog.Warn($"[helper-pipe] shutdown write failed (session {conn.SessionId}, {ex.GetType().Name})");
            throw;
        }
    }

    public static Task NotifyOverlayPrefsChangedAsync(HelperRegistry registry, CancellationToken ct = default)
    {
        var conn = registry.GetAny();
        if (conn is null) return Task.CompletedTask;
        return conn.SendAsync(
            type: "overlay.prefsChanged",
            payload: new OverlayPrefsChangedPayload(),
            payloadType: AppJsonContext.Default.OverlayPrefsChangedPayload,
            ct: ct);
    }
}

// ---------- Helper-side handler ----------

/// <summary>
/// Helper-side handler for lifecycle envelopes. The helper bootstrap
/// constructs this with concrete actions and calls
/// <see cref="Register"/> to bind them to the dispatch registry.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LifecycleHandler
{
    private readonly Action _onShutdown;
    private readonly Action _onOverlayPrefsChanged;

    public LifecycleHandler(Action onShutdown, Action onOverlayPrefsChanged)
    {
        _onShutdown = onShutdown;
        _onOverlayPrefsChanged = onOverlayPrefsChanged;
    }

    public void Register(HelperHandlerRegistry registry)
    {
        registry.Register("helper.shutdown", (env, _) =>
        {
            try { _onShutdown(); } catch { }
            return Task.FromResult(env.Ok());
        });
        registry.Register("overlay.prefsChanged", (env, _) =>
        {
            try { _onOverlayPrefsChanged(); } catch { }
            return Task.FromResult(env.Ok());
        });
    }
}
#endif
