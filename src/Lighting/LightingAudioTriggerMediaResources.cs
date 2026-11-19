using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Effects;
using Nexus.Service.Media;

namespace Nexus.Service.Lighting;

/// <summary>One bounded snapshot/window owner for all configured Audio Trigger media looks.</summary>
internal sealed class LightingAudioTriggerMediaResources : IDisposable
{
    internal const int MaximumBytes = MediaImporter.CanvasWidth * MediaImporter.CanvasHeight * 3 * MediaImporter.MaxFrames;
    private readonly AudioTriggerMediaSession _session;

    internal LightingAudioTriggerMediaResources(MediaLibrary media, Func<string, CancellationToken, Task>? beforeLoad = null)
        => _session = new(media, beforeLoad);

    internal Task PendingLoad => _session.PendingLoad;
    internal void ReconcileDemand(IReadOnlyCollection<string> ids) => _session.ReconcileDemand(ids);
    internal bool TryRender(string id, CanvasBuffer canvas, double tick) => _session.TryRender(id, canvas, tick);
    internal void Reset() => _session.Reset();
    public void Dispose() => _session.Dispose();
}

/// <summary>The look owns no media bytes. A pending read preserves the controller's last complete output.</summary>
internal sealed class LightingAudioTriggerMediaEffect(LightingAudioTriggerMediaResources resources, string id) : IEffect
{
    public string Name => "media";
    internal bool DrewFrame { get; private set; }
    public void RenderFrame(CanvasBuffer canvas, double tickMs) => DrewFrame = resources.TryRender(id, canvas, tickMs);
    public void Dispose() { }
}
