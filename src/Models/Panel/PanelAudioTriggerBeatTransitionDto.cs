using System.Text.Json.Serialization;

namespace Nexus.Service.Models.Panel;

/// <summary>Independent beat-to-beat pacing; effect selection remains shared with normal transitions.</summary>
public sealed class PanelAudioTriggerBeatTransitionDto
{
    public bool Enabled { get; set; }
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public int DurationMs { get; set; } = 800;
}
