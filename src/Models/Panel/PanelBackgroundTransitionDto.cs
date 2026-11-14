using System.Text.Json.Serialization;

namespace Nexus.Service.Models.Panel;

/// <summary>Independent Slideshow or Audio Trigger transition preference; absent means legacy behavior.</summary>
public sealed class PanelBackgroundTransitionDto
{
    public string Effect { get; set; } = "default";
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public int DurationMs { get; set; } = 800;
}
