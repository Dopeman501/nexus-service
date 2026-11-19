using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Effects;

namespace Nexus.Service.Tests.Lighting;

public sealed class LightingAudioTriggerMediaConcurrencyTests
{
    [Fact]
    public async Task A_cached_peer_cannot_cancel_another_still_active_media_load()
    {
        using var fixture = new LightingAudioTriggerMediaTests.LibraryFixture();
        fixture.Write(160, 90, 1, 0, id: "cold");
        fixture.Write(160, 90, 1, 0, id: "cached");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingToken = CancellationToken.None;
        using var resources = new LightingAudioTriggerMediaResources(fixture.Library, async (id, token) =>
        {
            if (id == "cold")
            {
                pendingToken = token;
                started.TrySetResult();
                await release.Task;
                token.ThrowIfCancellationRequested();
            }
        });
        var canvas = new CanvasBuffer();
        resources.ReconcileDemand(new[] { "cached" });
        resources.TryRender("cached", canvas, 0);
        await resources.PendingLoad.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(resources.TryRender("cached", canvas, 0));
        resources.ReconcileDemand(new[] { "cold", "cached" });
        resources.TryRender("cold", canvas, 1);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            Assert.True(resources.TryRender("cached", canvas, 1));
            Assert.False(pendingToken.IsCancellationRequested);
        }
        finally
        {
            release.TrySetResult();
            await resources.PendingLoad.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.True(resources.TryRender("cold", canvas, 2));
    }
}
